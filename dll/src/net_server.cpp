#define FD_SETSIZE 128
#include "net_server.h"

#include <winsock2.h>
#include <ws2tcpip.h>

#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <list>

namespace dcb {
namespace {

constexpr size_t kMaxClients = 100;
constexpr size_t kMaxPreAuthInput = 4096;
constexpr size_t kMaxInput = 64 * 1024;
constexpr auto kAuthTimeout = std::chrono::seconds(10);
constexpr char kAuthFail[] = "{\"type\":\"auth_fail\"}\n";
const char* const kCoalitionNames[CoalitionCount] = {"blue", "red"};

// Command limits: a controller cannot flood the sim thread or players' screens.
constexpr size_t kMaxQueuedCommands = 100;
constexpr size_t kMaxTextBytes = 1500;
constexpr size_t kMaxMarkBytes = 200;
constexpr int kBurst = 5;                          // commands at once
constexpr int kPerMinute = 30;                     // commands per minute

enum class ClientState { Pending, Authed, Closing };

struct Client {
    SOCKET socket;
    std::string address;
    uint64_t serial = 0;
    std::string in;
    std::string out;
    std::string results;  // command results waiting to be sent (before pictures)
    ClientState state = ClientState::Pending;
    Coalition coalition = Blue;
    uint64_t sentVersion = 0;
    std::map<std::string, uint64_t> sentChannels;
    std::chrono::steady_clock::time_point connectedAt = std::chrono::steady_clock::now();
    std::deque<std::chrono::steady_clock::time_point> recentCommands;
};

std::string jsonEscape(const std::string& s) {
    std::string out;
    for (unsigned char c : s) {
        if (c == '"' || c == '\\') { out += '\\'; out += static_cast<char>(c); }
        else if (c < 0x20) { char buf[8]; snprintf(buf, sizeof(buf), "\\u%04x", c); out += buf; }
        else out += static_cast<char>(c);
    }
    return out;
}

std::string resultJson(int64_t id, bool ok, const std::string& error) {
    return "{\"type\":\"command_result\",\"id\":" + std::to_string(id) + ",\"ok\":" + (ok ? "true" : "false") +
           (ok ? (error.empty() ? std::string() : ",\"value\":\"" + jsonEscape(error) + "\"")
               : ",\"error\":\"" + jsonEscape(error) + "\"") + "}\n";
}

bool equalConstantTime(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    unsigned char diff = 0;
    for (size_t i = 0; i < a.size(); ++i) diff |= static_cast<unsigned char>(a[i] ^ b[i]);
    return diff == 0;
}

void appendUtf8(std::string& out, unsigned cp) {
    if (cp < 0x80) {
        out += static_cast<char>(cp);
    } else if (cp < 0x800) {
        out += static_cast<char>(0xC0 | (cp >> 6));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

// Reads the string value of "key" from a flat JSON object. Enough for the auth message;
// not a general parser.
bool extractJsonString(const std::string& json, const char* key, std::string& value) {
    const std::string needle = std::string("\"") + key + "\"";
    size_t p = json.find(needle);
    if (p == std::string::npos) return false;
    p += needle.size();
    while (p < json.size() && (json[p] == ' ' || json[p] == '\t')) ++p;
    if (p >= json.size() || json[p] != ':') return false;
    ++p;
    while (p < json.size() && (json[p] == ' ' || json[p] == '\t')) ++p;
    if (p >= json.size() || json[p] != '"') return false;
    ++p;
    value.clear();
    while (p < json.size()) {
        char ch = json[p++];
        if (ch == '"') return true;
        if (ch != '\\') { value += ch; continue; }
        if (p >= json.size()) return false;
        char esc = json[p++];
        switch (esc) {
            case '"': case '\\': case '/': value += esc; break;
            case 'n': value += '\n'; break;
            case 't': value += '\t'; break;
            case 'r': value += '\r'; break;
            case 'b': value += '\b'; break;
            case 'f': value += '\f'; break;
            case 'u': {
                // Parsed by hand: an exception on the network thread would take DCS down.
                if (p + 4 > json.size()) return false;
                unsigned cp = 0;
                for (int i = 0; i < 4; ++i) {
                    char h = json[p++];
                    cp <<= 4;
                    if (h >= '0' && h <= '9') cp |= h - '0';
                    else if (h >= 'a' && h <= 'f') cp |= h - 'a' + 10;
                    else if (h >= 'A' && h <= 'F') cp |= h - 'A' + 10;
                    else return false;
                }
                appendUtf8(value, cp);
                break;
            }
            default: return false;
        }
    }
    return false;
}

// Reads the integer value of "key" from a flat JSON object (no exponent, no fraction).
bool extractJsonInteger(const std::string& json, const char* key, int64_t& value) {
    const std::string needle = std::string("\"") + key + "\"";
    size_t p = json.find(needle);
    if (p == std::string::npos) return false;
    p += needle.size();
    while (p < json.size() && (json[p] == ' ' || json[p] == '\t')) ++p;
    if (p >= json.size() || json[p] != ':') return false;
    ++p;
    while (p < json.size() && (json[p] == ' ' || json[p] == '\t')) ++p;
    bool negative = p < json.size() && json[p] == '-';
    if (negative) ++p;
    if (p >= json.size() || json[p] < '0' || json[p] > '9') return false;
    int64_t v = 0;
    int digits = 0;
    while (p < json.size() && json[p] >= '0' && json[p] <= '9') {
        if (++digits > 15) return false;  // out of any range we accept
        v = v * 10 + (json[p++] - '0');
    }
    value = negative ? -v : v;
    return true;
}

// Checks a command line from an authenticated client. Returns an empty string and fills cmd,
// or the reason it is refused.
std::string parseCommand(const std::string& line, Command& cmd) {
    if (!extractJsonInteger(line, "id", cmd.id)) return "missing id";
    if (!extractJsonString(line, "name", cmd.name)) return "missing name";
    if (cmd.name == "message_unit") {
        int64_t seconds = 0;
        if (!extractJsonInteger(line, "unit", cmd.unit) || cmd.unit <= 0) return "unit: a DCS unit id";
        if (!extractJsonString(line, "text", cmd.text) || cmd.text.empty()) return "text missing";
        if (cmd.text.size() > kMaxTextBytes) return "text longer than 1500 bytes";
        for (char& ch : cmd.text) {
            if (ch == '\r' || ch == '\t') ch = ' ';
            else if (static_cast<unsigned char>(ch) < 0x20 && ch != '\n') return "text has control characters";
        }
        if (!extractJsonInteger(line, "seconds", seconds) || seconds < 5 || seconds > 120) return "seconds: 5 to 120";
        cmd.seconds = static_cast<int>(seconds);
        return {};
    }
    if (cmd.name == "mark_add") {
        // An F10 mark for the controller's coalition: a short single-line text at a map point.
        if (!extractJsonInteger(line, "x", cmd.x) || !extractJsonInteger(line, "z", cmd.z) ||
            cmd.x < -2000000 || cmd.x > 2000000 || cmd.z < -2000000 || cmd.z > 2000000)
            return "x, z: DCS map metres";
        if (!extractJsonString(line, "text", cmd.text) || cmd.text.empty()) return "text missing";
        if (cmd.text.size() > kMaxMarkBytes) return "text longer than 200 bytes";
        for (char ch : cmd.text)
            if (static_cast<unsigned char>(ch) < 0x20) return "text has control characters";
        extractJsonString(line, "threat", cmd.threat);  // optional
        if (cmd.threat.size() > 24) return "threat: at most 24 characters";
        for (char ch : cmd.threat)
            if (!std::isalnum(static_cast<unsigned char>(ch)) && ch != '-' && ch != ' ' && ch != '/')
                return "threat: letters, digits, '-', '/' and spaces only";
        return {};
    }
    if (cmd.name == "mark_remove") {
        if (!extractJsonInteger(line, "mark", cmd.mark) || cmd.mark <= 0) return "mark: an F10 mark id";
        return {};
    }
    if (cmd.name == "carrier_lights") {
        // A carrier's lighting mode, as the Mission Editor's SET CARRIER LIGHTING MODE:
        // -2 off, -1 auto, 0 navigation, 1 launch, 2 recovery.
        int64_t mode = 0;
        if (!extractJsonInteger(line, "unit", cmd.unit) || cmd.unit <= 0) return "unit: a DCS unit id";
        if (!extractJsonInteger(line, "mode", mode) || mode < -2 || mode > 2) return "mode: -2 to 2";
        cmd.mode = static_cast<int>(mode);
        return {};
    }
    if (cmd.name == "threat_add") {
        // A threat the controller placed, shared with the coalition's controllers (not an F10
        // mark): a threat type at a map point. The hook answers with its id.
        if (!extractJsonInteger(line, "x", cmd.x) || !extractJsonInteger(line, "z", cmd.z) ||
            cmd.x < -2000000 || cmd.x > 2000000 || cmd.z < -2000000 || cmd.z > 2000000)
            return "x, z: DCS map metres";
        if (!extractJsonString(line, "threat", cmd.threat) || cmd.threat.empty()) return "threat missing";
        if (cmd.threat.size() > 24) return "threat: at most 24 characters";
        for (char ch : cmd.threat)
            if (!std::isalnum(static_cast<unsigned char>(ch)) && ch != '-' && ch != ' ' && ch != '/')
                return "threat: letters, digits, '-', '/' and spaces only";
        return {};
    }
    if (cmd.name == "nctr") {
        // NCTR on the contacts in an area (two corners, DCS metres, at most 400 km a side) with
        // the methods named in "methods" ("jem,hrrp,isar,size,rotor"), kept in cmd.text.
        auto inMap = [](int64_t v) { return v >= -2000000 && v <= 2000000; };
        if (!extractJsonInteger(line, "x", cmd.x) || !extractJsonInteger(line, "z", cmd.z) ||
            !extractJsonInteger(line, "x2", cmd.x2) || !extractJsonInteger(line, "z2", cmd.z2) ||
            !inMap(cmd.x) || !inMap(cmd.z) || !inMap(cmd.x2) || !inMap(cmd.z2))
            return "x, z, x2, z2: DCS map metres";
        if (std::llabs(cmd.x2 - cmd.x) > 400000 || std::llabs(cmd.z2 - cmd.z) > 400000) return "area: at most 400 km a side";
        if (!extractJsonString(line, "methods", cmd.text) || cmd.text.empty()) return "methods missing";
        if (cmd.text.size() > 40) return "methods: at most 40 characters";
        for (char ch : cmd.text)
            if (!(ch >= 'a' && ch <= 'z') && ch != ',') return "methods: lower-case names separated by commas";
        return {};
    }
    if (cmd.name == "threat_remove") {
        if (!extractJsonInteger(line, "threat_id", cmd.mark) || cmd.mark <= 0) return "threat_id: a shared threat id";
        return {};
    }
    if (cmd.name == "identify") {
        // An identity for an enemy contact, shared with the coalition's controllers: an RWR
        // label or type name ("29", "SA-6", "Su-34"); empty clears it. Kept in cmd.text.
        if (!extractJsonInteger(line, "unit", cmd.unit) || cmd.unit <= 0) return "unit: a DCS unit id";
        if (!extractJsonString(line, "label", cmd.text)) return "label missing";
        if (cmd.text.size() > 24) return "label: at most 24 characters";
        for (char ch : cmd.text)
            if (!std::isalnum(static_cast<unsigned char>(ch)) && ch != '-' && ch != ' ' && ch != '/')
                return "label: letters, digits, '-', '/' and spaces only";
        return {};
    }
    return "unknown command";
}

std::string peerAddress(const sockaddr_in& addr) {
    char buf[INET_ADDRSTRLEN] = {};
    inet_ntop(AF_INET, &addr.sin_addr, buf, sizeof(buf));
    return std::string(buf) + ":" + std::to_string(ntohs(addr.sin_port));
}

}  // namespace

NetServer::~NetServer() { stop(); }

bool NetServer::start(uint16_t port, std::string bluePassword, std::string redPassword, std::string& error) {
    if (running_) return true;

    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        error = "WSAStartup failed";
        return false;
    }
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) {
        error = "socket() failed: " + std::to_string(WSAGetLastError());
        WSACleanup();
        return false;
    }
    // A restarted DCS must not be able to share the port with a stale process.
    BOOL exclusive = TRUE;
    setsockopt(s, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(port);
    if (bind(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR ||
        listen(s, SOMAXCONN) == SOCKET_ERROR) {
        error = "bind/listen on port " + std::to_string(port) + " failed: " + std::to_string(WSAGetLastError());
        closesocket(s);
        WSACleanup();
        return false;
    }
    u_long nonBlocking = 1;
    ioctlsocket(s, FIONBIO, &nonBlocking);

    listenSocket_ = s;
    passwords_[Blue] = std::move(bluePassword);
    passwords_[Red] = std::move(redPassword);
    stopRequested_ = false;
    running_ = true;
    thread_ = std::thread(&NetServer::run, this);
    log("listening on port " + std::to_string(port));
    return true;
}

void NetServer::stop() {
    if (!running_) return;
    stopRequested_ = true;
    if (thread_.joinable()) thread_.join();
    closesocket(static_cast<SOCKET>(listenSocket_));
    listenSocket_ = ~uintptr_t(0);
    WSACleanup();
    running_ = false;
}

void NetServer::publish(Coalition c, std::string json) {
    json += '\n';
    std::lock_guard lock(pictureMutex_);
    pictures_[c] = std::move(json);
    ++pictureVersion_[c];
}

void NetServer::publishAll(const std::string& channel, std::string json) {
    json += '\n';
    auto data = std::make_shared<const std::string>(std::move(json));
    std::lock_guard lock(pictureMutex_);
    auto& ch = channels_[channel];
    ch.data = std::move(data);
    ++ch.version;
}

void NetServer::publishSide(Coalition c, const std::string& channel, std::string json) {
    json += '\n';
    auto data = std::make_shared<const std::string>(std::move(json));
    std::lock_guard lock(pictureMutex_);
    auto& ch = channels_[channel + "@" + kCoalitionNames[c]];
    ch.data = std::move(data);
    ch.side = c;
    ++ch.version;
}

std::vector<std::string> NetServer::drainLog() {
    std::lock_guard lock(logMutex_);
    std::vector<std::string> lines;
    lines.swap(logLines_);
    return lines;
}

std::vector<Command> NetServer::drainCommands() {
    std::lock_guard lock(commandMutex_);
    std::vector<Command> out;
    out.swap(commands_);
    return out;
}

void NetServer::commandResult(uint64_t client, int64_t id, bool ok, const std::string& error) {
    std::lock_guard lock(commandMutex_);
    if (results_.size() < 1000) results_.emplace_back(client, resultJson(id, ok, error));
}

void NetServer::log(std::string line) {
    std::lock_guard lock(logMutex_);
    if (logLines_.size() < 1000) logLines_.push_back(std::move(line));
}

void NetServer::run() {
    const SOCKET listener = static_cast<SOCKET>(listenSocket_);
    std::list<Client> clients;
    std::string latest[CoalitionCount];
    uint64_t latestVersion[CoalitionCount] = {};
    std::map<std::string, Channel> channels;
    uint64_t nextSerial = 1;

    while (!stopRequested_) {
        {
            std::lock_guard lock(pictureMutex_);
            for (int c = 0; c < CoalitionCount; ++c) {
                if (latestVersion[c] != pictureVersion_[c]) {
                    latest[c] = pictures_[c];
                    latestVersion[c] = pictureVersion_[c];
                }
            }
            channels = channels_;  // copies shared pointers only
        }
        {
            // Results from the sim thread go to the client that sent the command, if still here.
            std::vector<std::pair<uint64_t, std::string>> results;
            {
                std::lock_guard lock(commandMutex_);
                results.swap(results_);
            }
            for (auto& [serial, json] : results)
                for (auto& cl : clients)
                    if (cl.serial == serial && cl.state == ClientState::Authed) cl.results += json;
        }

        // Queue the next message only once the previous one is fully sent: a slow client
        // skips intermediate pictures instead of growing an unbounded buffer. Command
        // results go first, then channel messages (theatre, terrain), each version once.
        for (auto& cl : clients) {
            if (cl.state != ClientState::Authed || !cl.out.empty()) continue;
            if (!cl.results.empty()) {
                cl.out.swap(cl.results);
                continue;
            }
            for (const auto& [name, ch] : channels) {
                if (ch.side >= 0) continue;  // coalition channels go after the picture
                auto& sent = cl.sentChannels[name];
                if (sent != ch.version && ch.data) {
                    cl.out = *ch.data;
                    sent = ch.version;
                    break;
                }
            }
            if (cl.out.empty() && cl.sentVersion != latestVersion[cl.coalition]) {
                cl.out = latest[cl.coalition];
                cl.sentVersion = latestVersion[cl.coalition];
            }
            if (cl.out.empty()) {
                for (const auto& [name, ch] : channels) {
                    if (ch.side != static_cast<int>(cl.coalition)) continue;
                    auto& sent = cl.sentChannels[name];
                    if (sent != ch.version && ch.data) {
                        cl.out = *ch.data;
                        sent = ch.version;
                        break;
                    }
                }
            }
        }

        fd_set readSet, writeSet;
        FD_ZERO(&readSet);
        FD_ZERO(&writeSet);
        FD_SET(listener, &readSet);
        for (auto& cl : clients) {
            FD_SET(cl.socket, &readSet);
            if (!cl.out.empty()) FD_SET(cl.socket, &writeSet);
        }
        timeval timeout{0, 100 * 1000};
        if (select(0, &readSet, &writeSet, nullptr, &timeout) == SOCKET_ERROR) {
            log("select failed: " + std::to_string(WSAGetLastError()));
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            continue;
        }

        if (FD_ISSET(listener, &readSet)) {
            sockaddr_in addr{};
            int len = sizeof(addr);
            SOCKET s = accept(listener, reinterpret_cast<sockaddr*>(&addr), &len);
            if (s != INVALID_SOCKET) {
                if (clients.size() >= kMaxClients) {
                    closesocket(s);
                    log("rejected " + peerAddress(addr) + ": too many clients");
                } else {
                    u_long nonBlocking = 1;
                    ioctlsocket(s, FIONBIO, &nonBlocking);
                    BOOL noDelay = TRUE;
                    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&noDelay), sizeof(noDelay));
                    clients.push_back(Client{s, peerAddress(addr), nextSerial++});
                }
            }
        }

        const auto now = std::chrono::steady_clock::now();
        for (auto it = clients.begin(); it != clients.end();) {
            Client& cl = *it;
            bool drop = false;

            if (FD_ISSET(cl.socket, &readSet)) {
                char buf[4096];
                int n = recv(cl.socket, buf, sizeof(buf), 0);
                if (n <= 0) {
                    drop = true;
                } else if (cl.state != ClientState::Closing) {
                    cl.in.append(buf, n);
                }
            }

            if (!drop && cl.state == ClientState::Pending) {
                size_t nl = cl.in.find('\n');
                if (nl != std::string::npos) {
                    std::string password;
                    int coalition = -1;
                    if (extractJsonString(cl.in.substr(0, nl), "password", password)) {
                        for (int c = 0; c < CoalitionCount; ++c) {
                            if (!passwords_[c].empty() && equalConstantTime(password, passwords_[c])) coalition = c;
                        }
                    }
                    cl.in.erase(0, nl + 1);
                    if (coalition < 0) {
                        cl.out = kAuthFail;
                        cl.state = ClientState::Closing;
                        log("auth failed from " + cl.address);
                    } else {
                        cl.coalition = static_cast<Coalition>(coalition);
                        cl.out = std::string("{\"type\":\"auth_ok\",\"coalition\":\"") + kCoalitionNames[coalition] + "\"}\n";
                        cl.state = ClientState::Authed;
                        log(std::string("controller connected from ") + cl.address + " (" + kCoalitionNames[coalition] + ")");
                    }
                } else if (cl.in.size() > kMaxPreAuthInput || now - cl.connectedAt > kAuthTimeout) {
                    log("dropped " + cl.address + ": no auth");
                    drop = true;
                }
            }
            // After login a client may send commands, one JSON object per line.
            if (!drop && cl.state == ClientState::Authed) {
                size_t nl;
                while ((nl = cl.in.find('\n')) != std::string::npos) {
                    std::string line = cl.in.substr(0, nl);
                    cl.in.erase(0, nl + 1);
                    std::string type;
                    if (!extractJsonString(line, "type", type) || type != "command") continue;
                    Command cmd;
                    std::string refused = parseCommand(line, cmd);
                    // Rate: at most kBurst within a second and kPerMinute within a minute.
                    while (!cl.recentCommands.empty() && now - cl.recentCommands.front() > std::chrono::minutes(1))
                        cl.recentCommands.pop_front();
                    if (refused.empty()) {
                        int lastSecond = 0;
                        for (auto t : cl.recentCommands) if (now - t < std::chrono::seconds(1)) ++lastSecond;
                        if (lastSecond >= kBurst || static_cast<int>(cl.recentCommands.size()) >= kPerMinute) refused = "too many commands, wait a moment";
                    }
                    if (refused.empty()) {
                        cmd.client = cl.serial;
                        cmd.coalition = cl.coalition;
                        cmd.address = cl.address;
                        std::lock_guard lock(commandMutex_);
                        if (commands_.size() >= kMaxQueuedCommands) refused = "server busy";
                        else commands_.push_back(std::move(cmd));
                    }
                    if (refused.empty()) {
                        cl.recentCommands.push_back(now);
                    } else {
                        cl.results += resultJson(cmd.id, false, refused);
                        log("command refused from " + cl.address + " (" + kCoalitionNames[cl.coalition] + "): " + refused);
                    }
                }
                if (cl.in.size() > kMaxInput) cl.in.clear();  // a line that never ends
            }

            // Sockets are non-blocking, so trying without waiting for writability is safe
            // and sends auth replies in the same pass.
            if (!drop && !cl.out.empty()) {
                int n = send(cl.socket, cl.out.data(), static_cast<int>(cl.out.size()), 0);
                if (n > 0) {
                    cl.out.erase(0, n);
                } else if (WSAGetLastError() != WSAEWOULDBLOCK) {
                    drop = true;
                }
            }
            if (cl.state == ClientState::Closing && cl.out.empty()) drop = true;

            if (drop) {
                if (cl.state == ClientState::Authed) log("controller disconnected: " + cl.address);
                closesocket(cl.socket);
                it = clients.erase(it);
            } else {
                ++it;
            }
        }
    }

    for (auto& cl : clients) closesocket(cl.socket);
}

}  // namespace dcb
