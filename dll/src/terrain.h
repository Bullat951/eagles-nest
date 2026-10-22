#pragma once
// DCS terrain grid (height + surface type on a regular x/z grid), assembled from rows the
// hook samples in the mission env, cached on disk so each terrain is sampled once.
#include <cstdint>
#include <string>
#include <vector>

namespace dcb {

class TerrainGrid {
public:
    // Starts a grid. Returns true when a matching cache file was loaded (complete at once).
    bool begin(std::string id, double x0, double z0, double step, int nx, int nz, std::string cachePath);
    // Row i (x = x0 + i*step): nz comma-separated heights (m) and nz surface-type digits.
    bool setRow(int i, const char* heightsCsv, const char* surfaces, size_t surfacesLen);
    bool complete() const;
    int rowsDone() const { return rowsDone_; }
    // Writes the cache file; returns false if it could not be written.
    bool saveCache() const;
    // {"type":"terrain",...} with base64 int16 heights and uint8 surfaces, row-major.
    std::string toJson() const;

private:
    std::string id_, cachePath_;
    double x0_ = 0, z0_ = 0, step_ = 0;
    int nx_ = 0, nz_ = 0, rowsDone_ = 0;
    std::vector<int16_t> heights_;
    std::vector<uint8_t> surfaces_;
    std::vector<bool> rowDone_;
};

}  // namespace dcb
