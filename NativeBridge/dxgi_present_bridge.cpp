#include <Windows.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>

namespace
{
using DxgiPresentFn = int32_t(__stdcall*)(void*, uint32_t, uint32_t);

constexpr int32_t kEPointer = static_cast<int32_t>(0x80004003u);
constexpr int32_t kEFail = static_cast<int32_t>(0x80004005u);
constexpr uint32_t kMaxSupportedJumpCount = 32;

enum class ResolveStatus : uint32_t
{
    Ok = 0,
    InvalidArgument = 1,
    Unreadable = 2,
    NonExecutable = 3,
    Cycle = 4,
    DepthExceeded = 5,
    UnsupportedJump = 6,
};

enum class JumpDecodeResult
{
    NotJump,
    Resolved,
    Invalid,
    Unsupported,
};

bool IsReadableProtection(DWORD protection) noexcept
{
    if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
        return false;
    switch (protection & 0xFFu)
    {
    case PAGE_READONLY:
    case PAGE_READWRITE:
    case PAGE_WRITECOPY:
    case PAGE_EXECUTE:
    case PAGE_EXECUTE_READ:
    case PAGE_EXECUTE_READWRITE:
    case PAGE_EXECUTE_WRITECOPY:
        return true;
    default:
        return false;
    }
}

bool IsExecutableProtection(DWORD protection) noexcept
{
    if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
        return false;
    switch (protection & 0xFFu)
    {
    case PAGE_EXECUTE:
    case PAGE_EXECUTE_READ:
    case PAGE_EXECUTE_READWRITE:
    case PAGE_EXECUTE_WRITECOPY:
        return true;
    default:
        return false;
    }
}

bool IsReadableRange(uintptr_t address, size_t size) noexcept
{
    if (address == 0 || size == 0 || address > UINTPTR_MAX - (size - 1))
        return false;

    const uintptr_t last = address + size - 1;
    uintptr_t cursor = address;
    while (cursor <= last)
    {
        MEMORY_BASIC_INFORMATION information{};
        if (VirtualQuery(
                reinterpret_cast<const void*>(cursor),
                &information,
                sizeof(information)) == 0 ||
            information.State != MEM_COMMIT ||
            !IsReadableProtection(information.Protect))
        {
            return false;
        }

        const uintptr_t regionBegin = reinterpret_cast<uintptr_t>(information.BaseAddress);
        if (information.RegionSize == 0 ||
            regionBegin > UINTPTR_MAX - information.RegionSize)
        {
            return false;
        }
        const uintptr_t regionEnd = regionBegin + information.RegionSize;
        if (cursor < regionBegin || cursor >= regionEnd)
            return false;
        if (last < regionEnd)
            return true;
        cursor = regionEnd;
    }
    return true;
}

bool IsExecutableAddress(uintptr_t address) noexcept
{
    MEMORY_BASIC_INFORMATION information{};
    return address != 0 &&
        VirtualQuery(
            reinterpret_cast<const void*>(address),
            &information,
            sizeof(information)) != 0 &&
        information.State == MEM_COMMIT &&
        IsExecutableProtection(information.Protect);
}

bool TryReadMemory(uintptr_t address, void* destination, size_t size) noexcept
{
    if (destination == nullptr || !IsReadableRange(address, size))
        return false;
    __try
    {
        std::memcpy(destination, reinterpret_cast<const void*>(address), size);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

template <typename T>
bool TryReadValue(uintptr_t address, T* destination) noexcept
{
    return TryReadMemory(address, destination, sizeof(T));
}

bool TryAddressAtOffset(uintptr_t address, size_t offset, uintptr_t* resultOut) noexcept
{
    if (resultOut == nullptr || address > UINTPTR_MAX - offset)
        return false;
    *resultOut = address + offset;
    return true;
}

template <typename T>
bool TryReadValueAtOffset(uintptr_t address, size_t offset, T* destination) noexcept
{
    uintptr_t source = 0;
    return TryAddressAtOffset(address, offset, &source) &&
        TryReadValue(source, destination);
}

bool TryReadMemoryAtOffset(
    uintptr_t address,
    size_t offset,
    void* destination,
    size_t size) noexcept
{
    uintptr_t source = 0;
    return TryAddressAtOffset(address, offset, &source) &&
        TryReadMemory(source, destination, size);
}

bool TryAddRelative(
    uintptr_t instructionEnd,
    int64_t displacement,
    uintptr_t* targetOut) noexcept
{
    if (targetOut == nullptr)
        return false;
    if (displacement >= 0)
    {
        const auto offset = static_cast<uintptr_t>(displacement);
        if (instructionEnd > UINTPTR_MAX - offset)
            return false;
        *targetOut = instructionEnd + offset;
        return true;
    }

    const auto magnitude = static_cast<uintptr_t>(
        static_cast<uint64_t>(-(displacement + 1)) + 1);
    if (instructionEnd < magnitude)
        return false;
    *targetOut = instructionEnd - magnitude;
    return true;
}

JumpDecodeResult DecodeEntryJump(uintptr_t address, uintptr_t* targetOut) noexcept
{
    std::array<uint8_t, 2> prefix{};
    if (targetOut == nullptr ||
        !TryReadMemory(address, prefix.data(), prefix.size()))
    {
        return JumpDecodeResult::Invalid;
    }

    if (prefix[0] == 0xE9)
    {
        int32_t displacement = 0;
        uintptr_t instructionEnd = 0;
        if (!TryReadValueAtOffset(address, 1, &displacement) ||
            !TryAddressAtOffset(address, 5, &instructionEnd) ||
            !TryAddRelative(instructionEnd, displacement, targetOut))
        {
            return JumpDecodeResult::Invalid;
        }
        return JumpDecodeResult::Resolved;
    }
    if (prefix[0] == 0xEB)
    {
        int8_t displacement = 0;
        uintptr_t instructionEnd = 0;
        if (!TryReadValueAtOffset(address, 1, &displacement) ||
            !TryAddressAtOffset(address, 2, &instructionEnd) ||
            !TryAddRelative(instructionEnd, displacement, targetOut))
        {
            return JumpDecodeResult::Invalid;
        }
        return JumpDecodeResult::Resolved;
    }

    uintptr_t pointerSlot = 0;
    if (prefix[0] == 0xFF && prefix[1] == 0x25)
    {
        int32_t displacement = 0;
        uintptr_t instructionEnd = 0;
        if (!TryReadValueAtOffset(address, 2, &displacement) ||
            !TryAddressAtOffset(address, 6, &instructionEnd) ||
            !TryAddRelative(instructionEnd, displacement, &pointerSlot) ||
            !TryReadValue(pointerSlot, targetOut))
        {
            return JumpDecodeResult::Invalid;
        }
        return JumpDecodeResult::Resolved;
    }
    if (prefix[0] == 0xFF && prefix[1] == 0x24)
    {
        uint8_t sib = 0;
        if (!TryReadValueAtOffset(address, 2, &sib))
            return JumpDecodeResult::Invalid;
        if (sib == 0x25)
        {
            int32_t absoluteDisplacement = 0;
            if (!TryReadValueAtOffset(address, 3, &absoluteDisplacement))
                return JumpDecodeResult::Invalid;
            pointerSlot = static_cast<uintptr_t>(
                static_cast<intptr_t>(absoluteDisplacement));
            if (!TryReadValue(pointerSlot, targetOut))
                return JumpDecodeResult::Invalid;
            return JumpDecodeResult::Resolved;
        }
    }
    if (prefix[0] == 0xFF && (prefix[1] & 0x38u) == 0x20u)
        return JumpDecodeResult::Unsupported;

    if ((prefix[0] & 0xF0u) == 0x40u && prefix[1] == 0xFF)
    {
        uint8_t modRm = 0;
        if (!TryReadValueAtOffset(address, 2, &modRm))
            return JumpDecodeResult::Invalid;
        const bool isJump = (prefix[0] & 0x04u) == 0 &&
            (modRm & 0x38u) == 0x20u;
        if (isJump && modRm == 0x25)
        {
            int32_t displacement = 0;
            uintptr_t instructionEnd = 0;
            if (!TryReadValueAtOffset(address, 3, &displacement) ||
                !TryAddressAtOffset(address, 7, &instructionEnd) ||
                !TryAddRelative(instructionEnd, displacement, &pointerSlot) ||
                !TryReadValue(pointerSlot, targetOut))
            {
                return JumpDecodeResult::Invalid;
            }
            return JumpDecodeResult::Resolved;
        }
        if (isJump)
            return JumpDecodeResult::Unsupported;
    }

    if ((prefix[0] == 0x48 || prefix[0] == 0x49) &&
        prefix[1] >= 0xB8 && prefix[1] <= 0xBF)
    {
        uintptr_t immediateTarget = 0;
        if (!TryReadValueAtOffset(address, 2, &immediateTarget))
            return JumpDecodeResult::Invalid;

        const uint8_t registerIndex = static_cast<uint8_t>(prefix[1] - 0xB8);
        if (prefix[0] == 0x48)
        {
            std::array<uint8_t, 2> suffix{};
            if (!TryReadMemoryAtOffset(address, 10, suffix.data(), suffix.size()))
                return JumpDecodeResult::Invalid;
            if (suffix[0] == 0xFF &&
                suffix[1] == static_cast<uint8_t>(0xE0 + registerIndex))
            {
                *targetOut = immediateTarget;
                return JumpDecodeResult::Resolved;
            }
        }
        else
        {
            std::array<uint8_t, 3> suffix{};
            if (!TryReadMemoryAtOffset(address, 10, suffix.data(), suffix.size()))
                return JumpDecodeResult::Invalid;
            if (suffix[0] == 0x41 && suffix[1] == 0xFF &&
                suffix[2] == static_cast<uint8_t>(0xE0 + registerIndex))
            {
                *targetOut = immediateTarget;
                return JumpDecodeResult::Resolved;
            }
        }
    }

    return JumpDecodeResult::NotJump;
}

void SetResolveOutputs(
    uint32_t jumpCount,
    ResolveStatus status,
    uint32_t* jumpCountOut,
    uint32_t* statusOut) noexcept
{
    if (jumpCountOut != nullptr)
        *jumpCountOut = jumpCount;
    if (statusOut != nullptr)
        *statusOut = static_cast<uint32_t>(status);
}

int CaptureExceptionCode(uint32_t code, uint32_t* destination) noexcept
{
    if (code != EXCEPTION_ACCESS_VIOLATION)
        return EXCEPTION_CONTINUE_SEARCH;
    if (destination != nullptr)
        *destination = code;
    return EXCEPTION_EXECUTE_HANDLER;
}
}

extern "C" __declspec(dllexport) uint64_t __cdecl
GBFRChatOverlay_ResolveHookChainTarget(
    uint64_t functionAddress,
    uint32_t maxJumpCount,
    uint32_t* jumpCountOut,
    uint32_t* statusOut)
{
    SetResolveOutputs(0, ResolveStatus::Ok, jumpCountOut, statusOut);
    if (functionAddress == 0 || maxJumpCount == 0 ||
        maxJumpCount > kMaxSupportedJumpCount)
    {
        SetResolveOutputs(
            0, ResolveStatus::InvalidArgument, jumpCountOut, statusOut);
        return 0;
    }

    uintptr_t current = static_cast<uintptr_t>(functionAddress);
    std::array<uintptr_t, kMaxSupportedJumpCount + 1> visited{};
    uint32_t visitedCount = 0;
    uint32_t jumpCount = 0;
    for (;;)
    {
        if (!IsExecutableAddress(current))
        {
            SetResolveOutputs(
                jumpCount, ResolveStatus::NonExecutable, jumpCountOut, statusOut);
            return 0;
        }
        if (std::find(
                visited.begin(),
                visited.begin() + visitedCount,
                current) != visited.begin() + visitedCount)
        {
            SetResolveOutputs(
                jumpCount, ResolveStatus::Cycle, jumpCountOut, statusOut);
            return 0;
        }
        visited[visitedCount++] = current;

        uintptr_t next = 0;
        const JumpDecodeResult decodeResult = DecodeEntryJump(current, &next);
        if (decodeResult == JumpDecodeResult::Invalid)
        {
            SetResolveOutputs(
                jumpCount, ResolveStatus::Unreadable, jumpCountOut, statusOut);
            return 0;
        }
        if (decodeResult == JumpDecodeResult::Unsupported)
        {
            SetResolveOutputs(
                jumpCount,
                ResolveStatus::UnsupportedJump,
                jumpCountOut,
                statusOut);
            return 0;
        }
        if (decodeResult == JumpDecodeResult::NotJump)
        {
            SetResolveOutputs(
                jumpCount, ResolveStatus::Ok, jumpCountOut, statusOut);
            return static_cast<uint64_t>(current);
        }
        if (jumpCount >= maxJumpCount)
        {
            SetResolveOutputs(
                jumpCount,
                ResolveStatus::DepthExceeded,
                jumpCountOut,
                statusOut);
            return 0;
        }

        current = next;
        ++jumpCount;
    }
}

extern "C" __declspec(dllexport) int32_t __cdecl
GBFRChatOverlay_InvokeOriginalPresent(
    uint64_t originalFunctionAddress,
    void* swapChain,
    uint32_t syncInterval,
    uint32_t presentFlags,
    uint32_t* exceptionCodeOut)
{
    if (exceptionCodeOut != nullptr)
        *exceptionCodeOut = 0;
    if (originalFunctionAddress == 0 || swapChain == nullptr)
        return kEPointer;

    const auto present = reinterpret_cast<DxgiPresentFn>(
        static_cast<uintptr_t>(originalFunctionAddress));
    __try
    {
        return present(swapChain, syncInterval, presentFlags);
    }
    __except (CaptureExceptionCode(
        static_cast<uint32_t>(GetExceptionCode()),
        exceptionCodeOut))
    {
        return kEFail;
    }
}
