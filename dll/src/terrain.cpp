#include "terrain.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace dcb {
namespace {

constexpr char kMagic[8] = {'D', 'C', 'B', 'T', 'E', 'R', '1', '\0'};

std::string base64(const uint8_t* data, size_t len) {
    static const char* tbl = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((len + 2) / 3 * 4);
    for (size_t i = 0; i < len; i += 3) {
        uint32_t n = uint32_t(data[i]) << 16;
        if (i + 1 < len) n |= uint32_t(data[i + 1]) << 8;
        if (i + 2 < len) n |= data[i + 2];
        out += tbl[(n >> 18) & 63];
        out += tbl[(n >> 12) & 63];
        out += i + 1 < len ? tbl[(n >> 6) & 63] : '=';
        out += i + 2 < len ? tbl[n & 63] : '=';
    }
    return out;
}

std::string jsonEscape(const std::string& s) {
    std::string out;
    for (char c : s) {
        if (c == '"' || c == '\\') out += '\\';
        if (static_cast<unsigned char>(c) >= 0x20) out += c;
    }
    return out;
}

struct Header {
    char magic[8];
    double x0, z0, step;
    int32_t nx, nz;
    char id[64];
};

}  // namespace

bool TerrainGrid::begin(std::string id, double x0, double z0, double step, int nx, int nz, std::string cachePath) {
    id_ = std::move(id);
    cachePath_ = std::move(cachePath);
    x0_ = x0;
    z0_ = z0;
    step_ = step;
    nx_ = nx;
    nz_ = nz;
    const size_t n = size_t(nx) * size_t(nz);
    heights_.assign(n, 0);
    surfaces_.assign(n, 0);
    rowDone_.assign(nx, false);
    rowsDone_ = 0;

    FILE* f = nullptr;
    if (fopen_s(&f, cachePath_.c_str(), "rb") != 0 || !f) return false;
    Header h{};
    bool ok = fread(&h, sizeof(h), 1, f) == 1 && std::memcmp(h.magic, kMagic, sizeof(kMagic)) == 0 &&
              h.x0 == x0 && h.z0 == z0 && h.step == step && h.nx == nx && h.nz == nz &&
              std::strncmp(h.id, id_.c_str(), sizeof(h.id)) == 0 &&
              fread(heights_.data(), sizeof(int16_t), n, f) == n && fread(surfaces_.data(), 1, n, f) == n;
    fclose(f);
    if (!ok) return false;
    rowDone_.assign(nx, true);
    rowsDone_ = nx;
    return true;
}

bool TerrainGrid::setRow(int i, const char* heightsCsv, const char* surfaces, size_t surfacesLen) {
    if (i < 0 || i >= nx_ || surfacesLen != size_t(nz_)) return false;
    const char* p = heightsCsv;
    for (int j = 0; j < nz_; ++j) {
        char* end = nullptr;
        long v = std::strtol(p, &end, 10);
        if (end == p) return false;
        heights_[size_t(i) * nz_ + j] = static_cast<int16_t>(v < -32768 ? -32768 : v > 32767 ? 32767 : v);
        p = *end == ',' ? end + 1 : end;
        char s = surfaces[j];
        surfaces_[size_t(i) * nz_ + j] = static_cast<uint8_t>(s >= '0' && s <= '9' ? s - '0' : 0);
    }
    if (!rowDone_[i]) {
        rowDone_[i] = true;
        ++rowsDone_;
    }
    return true;
}

bool TerrainGrid::complete() const { return nx_ > 0 && rowsDone_ == nx_; }

bool TerrainGrid::saveCache() const {
    FILE* f = nullptr;
    if (fopen_s(&f, cachePath_.c_str(), "wb") != 0 || !f) return false;
    Header h{};
    std::memcpy(h.magic, kMagic, sizeof(kMagic));
    h.x0 = x0_;
    h.z0 = z0_;
    h.step = step_;
    h.nx = nx_;
    h.nz = nz_;
    strncpy_s(h.id, id_.c_str(), _TRUNCATE);
    bool ok = fwrite(&h, sizeof(h), 1, f) == 1 && fwrite(heights_.data(), sizeof(int16_t), heights_.size(), f) == heights_.size() &&
              fwrite(surfaces_.data(), 1, surfaces_.size(), f) == surfaces_.size();
    fclose(f);
    return ok;
}

std::string TerrainGrid::toJson() const {
    char head[512];
    snprintf(head, sizeof(head),
             "{\"type\":\"terrain\",\"id\":\"%s\",\"x0\":%.1f,\"z0\":%.1f,\"step\":%.1f,\"nx\":%d,\"nz\":%d,\"heights\":\"",
             jsonEscape(id_).c_str(), x0_, z0_, step_, nx_, nz_);
    std::string json = head;
    json += base64(reinterpret_cast<const uint8_t*>(heights_.data()), heights_.size() * sizeof(int16_t));
    json += "\",\"surfaces\":\"";
    json += base64(surfaces_.data(), surfaces_.size());
    json += "\"}";
    return json;
}

}  // namespace dcb
