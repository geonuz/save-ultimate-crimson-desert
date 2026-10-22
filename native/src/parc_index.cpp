// SaveUltimate compact relocation index. The original validation DLL stays separate.
#include "save_parser_cpp.h"
#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <unordered_set>

using namespace SaveParserCpp;

namespace {
uint32_t read32(const std::vector<uint8_t>& raw, uint32_t pos) {
    if (uint64_t(pos) + 4 > raw.size()) throw std::runtime_error("Index position outside stream");
    uint32_t value;
    std::memcpy(&value, raw.data() + pos, 4);
    return value;
}

void copy_result(const void* data, size_t size, void** output, uint32_t* length) {
    if (size > std::numeric_limits<uint32_t>::max()) throw std::runtime_error("Index too large");
    void* allocated = std::malloc(std::max<size_t>(size, 1));
    if (!allocated) throw std::bad_alloc();
    if (size) std::memcpy(allocated, data, size);
    *output = allocated;
    *length = static_cast<uint32_t>(size);
}
std::vector<CompactLink> checked_links(const ParseResult& parsed) {
    const auto& raw = parsed.raw_blob;
    std::vector<CompactLink> links;
    std::unordered_set<uint32_t> positions;
    for (const auto& block : parsed.objects) for (const auto& field : block.fields) {
        for (const auto& link : field.compact_links) {
            if (read32(raw, link.position) != link.payload || !positions.insert(link.position).second)
                throw std::runtime_error("Unverified or duplicate inline pointer");
            if (!link.size_position || link.size_position < link.payload
                || read32(raw, link.size_position) != link.size_position - link.payload)
                throw std::runtime_error("Unverified inline size");
            links.push_back(link);
        }
    }
    const uint8_t sentinel[8] = {255,255,255,255,255,255,255,255};
    auto cursor = raw.begin();
    while ((cursor = std::search(cursor, raw.end(), sentinel, sentinel + 8)) != raw.end()) {
        uint32_t pos = static_cast<uint32_t>(cursor - raw.begin()) + 8;
        if (uint64_t(pos) + 4 <= raw.size() && read32(raw, pos) == pos + 4 && !positions.count(pos))
            throw std::runtime_error("Unparsed inline pointer; insertion requires parser review");
        ++cursor;
    }
    return links;
}

void write32(std::vector<uint8_t>& raw, uint32_t position, uint32_t value) {
    if (uint64_t(position) + 4 > raw.size()) throw std::runtime_error("Relocation outside stream");
    std::memcpy(raw.data() + position, &value, 4);
}
}

extern "C" {
__declspec(dllexport) uint32_t parc_index_version() { return 4; }
__declspec(dllexport) void parc_index_free(void* memory) { std::free(memory); }

// The packet contains repeated (start, stop, byte count, replacement bytes).
__declspec(dllexport) int parc_replace_regions(const uint8_t* data, uint32_t size,
        const uint8_t* packet_data, uint32_t packet_size, void** output, uint32_t* length) {
    if (!output || !length) return -2;
    *output = nullptr;
    *length = 0;
    try {
        if (!data || !size || !packet_data || !packet_size) throw std::runtime_error("Empty replacement input");
        auto parsed = ParseCompactBytes(std::vector<uint8_t>(data, data + size));
        const auto links = checked_links(parsed);
        // Only the checked links and TOC are needed while assembling the candidate.
        std::vector<uint8_t>().swap(parsed.raw_blob);
        std::vector<ObjectBlock>().swap(parsed.objects);
        const std::vector<uint8_t> packet(packet_data, packet_data + packet_size);
        std::vector<uint8_t> candidate;
        std::vector<uint32_t> starts, ends;
        std::vector<std::pair<uint32_t, uint32_t>> payloads;
        std::vector<int64_t> shifts{0};
        uint32_t cursor = 0;
        for (uint64_t at = 0; at < packet.size();) {
            if (at + 12 > packet.size()) throw std::runtime_error("Truncated replacement header");
            const auto start = read32(packet, at), stop = read32(packet, at + 4), count = read32(packet, at + 8);
            at += 12;
            if (start < cursor || stop < start || stop > size || at + count > packet.size()
                    || start < uint64_t(parsed.schema.schema_end) + 12 + 20ULL * parsed.toc.entry_count
                    || (!starts.empty() && start == starts.back()))
                throw std::runtime_error("Overlapping or invalid record replacements");
            const int64_t shift = shifts.back() + int64_t(count) - (stop - start);
            if (int64_t(size) + shift <= 0 || int64_t(size) + shift > UINT32_MAX)
                throw std::runtime_error("Invalid relocated stream size");
            starts.push_back(start); ends.push_back(stop); shifts.push_back(shift);
            payloads.emplace_back(static_cast<uint32_t>(at), count);
            cursor = stop; at += count;
        }
        candidate.reserve(static_cast<size_t>(int64_t(size) + shifts.back()));
        cursor = 0;
        for (size_t i = 0; i < starts.size(); ++i) {
            candidate.insert(candidate.end(), data + cursor, data + starts[i]);
            const auto [at, count] = payloads[i];
            candidate.insert(candidate.end(), packet.begin() + at, packet.begin() + at + count);
            cursor = ends[i];
        }
        candidate.insert(candidate.end(), data + cursor, data + size);
        auto inside = [&](uint32_t position) {
            auto it = std::upper_bound(starts.begin(), starts.end(), position);
            return it != starts.begin() && position < ends[(it - starts.begin()) - 1];
        };
        auto moved = [&](uint32_t position) -> uint32_t {
            const int64_t value = int64_t(position) + shifts[std::upper_bound(ends.begin(), ends.end(), position) - ends.begin()];
            if (value < 0 || uint64_t(value) > candidate.size()) throw std::runtime_error("Invalid relocated address");
            return static_cast<uint32_t>(value);
        };
        for (const auto& link : links) {
            if (!inside(link.position)) {
                if (inside(link.payload)) throw std::runtime_error("External pointer into replaced list");
                write32(candidate, moved(link.position), moved(link.payload));
            }
            if (!inside(link.size_position)) {
                if (inside(link.payload)) throw std::runtime_error("External size for replaced list");
                const auto position = moved(link.size_position), payload = moved(link.payload);
                if (position < payload) throw std::runtime_error("Invalid relocated record size");
                write32(candidate, position, position - payload);
            }
        }
        for (const auto& entry : parsed.toc.entries) {
            const auto start = moved(entry.data_offset), end = moved(entry.data_offset + entry.data_size);
            if (end < start) throw std::runtime_error("Invalid relocated block size");
            write32(candidate, entry.entry_offset + 12, start);
            write32(candidate, entry.entry_offset + 16, end - start);
        }
        write32(candidate, parsed.schema.schema_end + 8, static_cast<uint32_t>(candidate.size()));
        copy_result(candidate.data(), candidate.size(), output, length);
        return 0;
    } catch (const std::exception& error) {
        try { copy_result(error.what(), std::strlen(error.what()), output, length); }
        catch (...) { return -3; }
        return -1;
    } catch (...) { return -3; }
}

// Packed uint32 triples: field index, start, end for present fields of one block.
__declspec(dllexport) int parc_field_spans(const uint8_t* data, uint32_t size, uint32_t class_index,
                                         void** output, uint32_t* length) {
    if (!output || !length) return -2;
    *output = nullptr;
    *length = 0;
    try {
        if (!data || !size) throw std::runtime_error("Empty save blob");
        auto parsed = ParseCompactBytes(std::vector<uint8_t>(data, data + size), class_index);
        if (parsed.objects.size() != 1) throw std::runtime_error("Expected one selected block");
        const auto& block = parsed.objects.front();
        std::vector<uint32_t> spans;
        for (const auto& field : block.fields) {
            if (!field.present) continue;
            if (field.start_offset < block.data_offset || field.end_offset <= field.start_offset
                || uint64_t(field.end_offset) > uint64_t(block.data_offset) + block.data_size)
                throw std::runtime_error("Incomplete selected block fields");
            spans.insert(spans.end(), {field.field_index, field.start_offset, field.end_offset});
        }
        copy_result(spans.data(), spans.size() * sizeof(uint32_t), output, length);
        return 0;
    } catch (const std::exception& error) {
        try { copy_result(error.what(), std::strlen(error.what()), output, length); }
        catch (...) { return -3; }
        return -1;
    } catch (...) { return -3; }
}

}
