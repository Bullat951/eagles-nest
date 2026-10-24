#pragma once
// TCP server for DCB controllers. Runs on its own thread; the DCS sim thread only
// calls publish()/drainLog(), which take a mutex briefly and never block on I/O.
#include <atomic>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace dcb {

enum Coalition { Blue = 0, Red = 1, CoalitionCount = 2 };

// A controller's command, checked on the network thread (whitelisted name, field limits, rate)
// and handed to the sim thread. Fields not used by a command stay empty/zero.
struct Command {
    uint64_t client = 0;        // serial of the sending connection, for the result
    Coalition coalition = Blue; // decided by the controller's password at login
    std::string address;        // for the audit log
    int64_t id = 0;             // the client's own id, echoed in the result
    std::string name;           // "message_unit", "mark_add", "mark_remove", "carrier_lights", "identify", "threat_add", "threat_remove"
    int64_t unit = 0;           // DCS unit id
    std::string text;           // message text, mark text, or the identify label
    int seconds = 0;
    int64_t x = 0, z = 0;       // DCS map metres (mark_add, threat_add; nctr: one corner)
    int64_t x2 = 0, z2 = 0;     // nctr: the other corner of the area
    std::string threat;         // threat type name, e.g. "SA-6" (mark_add, may be empty)
    int64_t mark = 0;           // F10 mark id (mark_remove), shared threat id (threat_remove)
    int mode = 0;               // carrier lighting mode, -2 off .. 2 recovery (carrier_lights)
};

class NetServer {
public:
    ~NetServer();

    // Empty password disables logins for that coalition.
    bool start(uint16_t port, std::string bluePassword, std::string redPassword, std::string& error);
    void stop();
    bool running() const { return running_; }

    // json: one complete JSON object without a trailing newline.
    void publish(Coalition c, std::string json);
    // Message for every coalition (theatre, terrain). Each authenticated client receives the
    // latest version of each channel once, before its next picture.
    void publishAll(const std::string& channel, std::string json);
    // Message for one coalition only on its own channel (e.g. the fast LSO feed): latest version
    // per channel, sent after that coalition's picture so it never holds the picture back.
    void publishSide(Coalition c, const std::string& channel, std::string json);
    std::vector<std::string> drainLog();
    // Commands waiting for the sim thread (oldest first).
    std::vector<Command> drainCommands();
    // Answers a command; dropped if that client has gone. On success a non-empty error
    // string is sent as the result's "value" instead (e.g. the new mark's id).
    void commandResult(uint64_t client, int64_t id, bool ok, const std::string& error);

private:
    void run();
    void log(std::string line);

    std::mutex commandMutex_;
    std::vector<Command> commands_;
    std::vector<std::pair<uint64_t, std::string>> results_;  // client serial, JSON line

    std::atomic<bool> running_{false};
    std::atomic<bool> stopRequested_{false};
    std::thread thread_;
    uintptr_t listenSocket_ = ~uintptr_t(0);
    std::string passwords_[CoalitionCount];

    std::mutex pictureMutex_;
    std::string pictures_[CoalitionCount];  // newline-terminated
    uint64_t pictureVersion_[CoalitionCount] = {};

    struct Channel {
        std::shared_ptr<const std::string> data;  // newline-terminated
        uint64_t version = 0;
        int side = -1;  // -1: every coalition; else only that one (sent after the picture)
    };
    std::map<std::string, Channel> channels_;

    std::mutex logMutex_;
    std::vector<std::string> logLines_;
};

}  // namespace dcb
