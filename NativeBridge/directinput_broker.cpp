#define DIRECTINPUT_VERSION 0x0800

#include <Windows.h>
#include <dinput.h>
#include <Xinput.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <intrin.h>
#include <mutex>
#include <utility>

#include "third_party/safetyhook.hpp"

namespace
{
constexpr uint32_t kBrokerAbiVersion = 2;
constexpr size_t kKeyboardWordCount = 4;
constexpr size_t kMaximumHotkeyBindings = 64;

constexpr uint32_t kPolicyCaptureKeyboard = 1u << 0;
constexpr uint32_t kPolicyCaptureMouse = 1u << 1;
constexpr uint32_t kPolicySuppressActivation = 1u << 2;
constexpr uint32_t kPolicySuppressSettings = 1u << 3;
constexpr uint32_t kPolicySuppressPushToTalk = 1u << 4;
constexpr uint32_t kPolicySuppressQuickActions = 1u << 5;
constexpr uint32_t kPolicyMask =
    kPolicyCaptureKeyboard |
    kPolicyCaptureMouse |
    kPolicySuppressActivation |
    kPolicySuppressSettings |
    kPolicySuppressPushToTalk |
    kPolicySuppressQuickActions;

constexpr uint32_t kReadyIat = 1u << 0;
constexpr uint32_t kReadyFactory = 1u << 1;
constexpr uint32_t kReadyKeyboard = 1u << 2;
constexpr uint32_t kReadyMouse = 1u << 3;
constexpr uint32_t kReadyXInput = 1u << 4;

constexpr size_t kCreateDeviceVtableIndex = 3;
constexpr size_t kGetDeviceStateVtableIndex = 9;
constexpr size_t kGetDeviceDataVtableIndex = 10;

constexpr GUID kDirectInputSystemMouse = {
    0x6F1D2B60,
    0xD5A0,
    0x11CF,
    {0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00}};
constexpr GUID kDirectInputSystemKeyboard = {
    0x6F1D2B61,
    0xD5A0,
    0x11CF,
    {0xBF, 0xC7, 0x44, 0x45, 0x53, 0x54, 0x00, 0x00}};
constexpr GUID kDirectInput8AInterface = {
    0xBF798030,
    0x483A,
    0x4DA2,
    {0xAA, 0x99, 0x5D, 0x64, 0xED, 0x36, 0x97, 0x00}};
constexpr GUID kDirectInput8WInterface = {
    0xBF798031,
    0x483A,
    0x4DA2,
    {0xAA, 0x99, 0x5D, 0x64, 0xED, 0x36, 0x97, 0x00}};

#pragma pack(push, 1)
struct DirectInputBrokerSnapshot
{
    uint32_t abi_version;
    uint32_t struct_size;
    uint64_t sequence;
    uint64_t keyboard_words[kKeyboardWordCount];
    uint16_t controller_buttons;
    uint16_t reserved;
    uint32_t ready_flags;
    uint32_t policy_flags;
    uint32_t active;
};

struct DirectInputHotkeyBinding
{
    uint8_t scan_code;
    uint8_t modifiers;
    uint8_t policy_flag;
    uint8_t reserved;
};
#pragma pack(pop)

static_assert(sizeof(DirectInputBrokerSnapshot) == 64);
static_assert(sizeof(DirectInputHotkeyBinding) == 4);

using DirectInput8CreateFn = HRESULT(WINAPI*)(
    HINSTANCE,
    DWORD,
    REFIID,
    LPVOID*,
    LPUNKNOWN);
using XInputGetStateFn = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);

std::mutex g_hook_mutex;
std::atomic<void*> g_original_direct_input8_create{nullptr};
std::atomic<void*> g_original_xinput_get_state{nullptr};
SafetyHookInline g_create_device_hook;
SafetyHookInline g_get_state_hook;
SafetyHookInline g_get_data_hook;
SafetyHookInline g_get_state_hook_secondary;
SafetyHookInline g_get_data_hook_secondary;
uintptr_t g_get_state_target = 0;
uintptr_t g_get_data_target = 0;
uintptr_t g_get_state_target_secondary = 0;
uintptr_t g_get_data_target_secondary = 0;
std::atomic_uintptr_t g_keyboard_device{0};
std::atomic_uintptr_t g_mouse_device{0};
std::atomic_uint32_t g_ready_flags{0};
std::atomic_uint32_t g_policy_flags{0};
std::array<std::atomic_uint64_t, kKeyboardWordCount> g_keyboard_words{};
std::array<std::atomic_uint64_t, kKeyboardWordCount> g_keyboard_drain_words{};
std::array<std::atomic_uint32_t, kMaximumHotkeyBindings> g_hotkey_bindings{};
std::atomic_uint32_t g_hotkey_binding_count{0};
std::array<std::atomic_uint16_t, XUSER_MAX_COUNT> g_controller_user_buttons{};
std::atomic_uint16_t g_controller_buttons{0};
std::atomic_uint64_t g_input_sequence{0};
std::atomic_bool g_active{false};
std::atomic_bool g_keyboard_drain{false};
std::atomic_bool g_mouse_drain{false};

// GBFR.ChatOverlay declares CanUnload=false. These hooks therefore live for the process lifetime;
// Reloaded suspend/resume changes only the atomic policy and never rewrites live code.

bool SafeReadPointer(uintptr_t address, uintptr_t& value) noexcept
{
    value = 0;
    if (address == 0)
        return false;
    __try
    {
        value = *reinterpret_cast<const uintptr_t*>(address);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        value = 0;
        return false;
    }
}

bool IsExecutableAddress(uintptr_t address) noexcept
{
    MEMORY_BASIC_INFORMATION information{};
    if (address == 0 ||
        VirtualQuery(
            reinterpret_cast<const void*>(address),
            &information,
            sizeof(information)) == 0)
    {
        return false;
    }

    const DWORD protection = information.Protect & 0xFF;
    return information.State == MEM_COMMIT &&
        (protection == PAGE_EXECUTE ||
         protection == PAGE_EXECUTE_READ ||
         protection == PAGE_EXECUTE_READWRITE ||
         protection == PAGE_EXECUTE_WRITECOPY);
}

bool InstallInlineHook(
    SafetyHookInline& destination,
    uintptr_t target,
    void* detour) noexcept
{
    try
    {
        auto candidate = safetyhook::create_inline(
            reinterpret_cast<void*>(target),
            detour,
            SafetyHookInline::StartDisabled);
        if (!candidate)
            return false;

        // Publish the trampoline-bearing object before making its detour reachable. SafetyHook's
        // StartDisabled mode prevents a game thread from entering a detour that cannot call back to
        // the original method yet.
        destination = std::move(candidate);
        const auto enabled = destination.enable();
        if (!enabled)
        {
            destination.reset();
            return false;
        }
        return true;
    }
    catch (...)
    {
        destination.reset();
        return false;
    }
}

bool PatchMainModuleImport(
    const char* module_name,
    const char* function_name,
    void* replacement,
    std::atomic<void*>& published_original) noexcept
{
    const auto image_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    if (image_base == 0 || module_name == nullptr || function_name == nullptr ||
        replacement == nullptr)
    {
        return false;
    }

    __try
    {
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image_base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
            return false;
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            image_base + static_cast<uintptr_t>(dos->e_lfanew));
        if (nt->Signature != IMAGE_NT_SIGNATURE)
            return false;
        const IMAGE_DATA_DIRECTORY& directory =
            nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (directory.VirtualAddress == 0 ||
            directory.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR))
        {
            return false;
        }

        auto* descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(
            image_base + directory.VirtualAddress);
        for (; descriptor->Name != 0; ++descriptor)
        {
            const char* imported_module = reinterpret_cast<const char*>(
                image_base + descriptor->Name);
            if (_stricmp(imported_module, module_name) != 0)
                continue;

            auto* first_thunk = reinterpret_cast<IMAGE_THUNK_DATA64*>(
                image_base + descriptor->FirstThunk);
            auto* name_thunk = descriptor->OriginalFirstThunk != 0
                ? reinterpret_cast<IMAGE_THUNK_DATA64*>(
                    image_base + descriptor->OriginalFirstThunk)
                : first_thunk;
            for (; name_thunk->u1.AddressOfData != 0; ++name_thunk, ++first_thunk)
            {
                if (IMAGE_SNAP_BY_ORDINAL64(name_thunk->u1.Ordinal))
                    continue;
                const auto* import = reinterpret_cast<const IMAGE_IMPORT_BY_NAME*>(
                    image_base + name_thunk->u1.AddressOfData);
                if (std::strcmp(
                        reinterpret_cast<const char*>(import->Name),
                        function_name) != 0)
                {
                    continue;
                }

                auto** slot = reinterpret_cast<void**>(&first_thunk->u1.Function);
                void* previous = *slot;
                if (previous == nullptr || previous == replacement)
                    return false;

                DWORD old_protection = 0;
                if (!VirtualProtect(
                        slot,
                        sizeof(void*),
                        PAGE_READWRITE,
                        &old_protection))
                {
                    return false;
                }

                // Publish the callable original before making the detour reachable from the game.
                // This closes the small IAT race where another thread could enter the replacement
                // between the pointer exchange and InstallBroker storing the original.
                published_original.store(previous, std::memory_order_release);
                void* observed = InterlockedCompareExchangePointer(
                    reinterpret_cast<void* volatile*>(slot),
                    replacement,
                    previous);
                DWORD ignored = 0;
                (void)VirtualProtect(
                    slot,
                    sizeof(void*),
                    old_protection,
                    &ignored);
                if (observed != previous)
                {
                    published_original.store(nullptr, std::memory_order_release);
                    return false;
                }

                return true;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        published_original.store(nullptr, std::memory_order_release);
        return false;
    }
    return false;
}

HRESULT InvokeDirectInput8CreateSafely(
    HINSTANCE instance,
    DWORD version,
    REFIID interface_id,
    LPVOID* output,
    LPUNKNOWN outer) noexcept
{
    const auto original = reinterpret_cast<DirectInput8CreateFn>(
        g_original_direct_input8_create.load(std::memory_order_acquire));
    if (original == nullptr)
        return DIERR_GENERIC;
    __try
    {
        return original(
            instance,
            version,
            interface_id,
            output,
            outer);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return DIERR_GENERIC;
    }
}

HRESULT InvokeCreateDeviceSafely(
    void* factory,
    const GUID* device_guid,
    void** output_device,
    void* outer) noexcept
{
    if (!g_create_device_hook)
        return DIERR_GENERIC;
    __try
    {
        return g_create_device_hook.call<HRESULT>(
            factory,
            device_guid,
            output_device,
            outer);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return DIERR_GENERIC;
    }
}

HRESULT InvokeGetDeviceStateSafely(
    SafetyHookInline& hook,
    void* device,
    DWORD data_size,
    void* data) noexcept
{
    if (!hook)
        return DIERR_GENERIC;
    __try
    {
        return hook.call<HRESULT>(device, data_size, data);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return DIERR_GENERIC;
    }
}

HRESULT InvokeGetDeviceDataSafely(
    SafetyHookInline& hook,
    void* device,
    DWORD object_data_size,
    void* object_data,
    DWORD* object_count,
    DWORD flags) noexcept
{
    if (!hook)
        return DIERR_GENERIC;
    __try
    {
        return hook.call<HRESULT>(
            device,
            object_data_size,
            object_data,
            object_count,
            flags);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return DIERR_GENERIC;
    }
}

std::array<uint64_t, kKeyboardWordCount> ReadKeyboardWords(
    const uint8_t* state,
    DWORD data_size) noexcept
{
    std::array<uint64_t, kKeyboardWordCount> words{};
    if (state == nullptr)
        return words;
    const DWORD maximum = std::min<DWORD>(data_size, 256);
    for (DWORD scan_code = 0; scan_code < maximum; ++scan_code)
    {
        if ((state[scan_code] & 0x80) != 0)
            words[scan_code / 64] |= uint64_t{1} << (scan_code % 64);
    }
    return words;
}

DWORD InvokeXInputGetStateSafely(DWORD user_index, XINPUT_STATE* state) noexcept
{
    const auto original = reinterpret_cast<XInputGetStateFn>(
        g_original_xinput_get_state.load(std::memory_order_acquire));
    if (original == nullptr)
        return ERROR_DEVICE_NOT_CONNECTED;
    __try
    {
        return original(user_index, state);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return ERROR_DEVICE_NOT_CONNECTED;
    }
}

uint16_t AggregateControllerButtons() noexcept
{
    uint16_t buttons = 0;
    for (size_t index = 0; index < XUSER_MAX_COUNT; ++index)
    {
        buttons |= g_controller_user_buttons[index].load(std::memory_order_acquire);
    }
    return buttons;
}

DWORD WINAPI XInputGetStateDetour(DWORD user_index, XINPUT_STATE* state)
{
    const DWORD result = InvokeXInputGetStateSafely(user_index, state);
    if (user_index < XUSER_MAX_COUNT)
    {
        const uint16_t buttons = result == ERROR_SUCCESS && state != nullptr
            ? state->Gamepad.wButtons
            : 0;
        g_controller_user_buttons[user_index].store(buttons, std::memory_order_release);
        const uint16_t aggregate = AggregateControllerButtons();
        if (g_controller_buttons.exchange(aggregate, std::memory_order_acq_rel) != aggregate)
            g_input_sequence.fetch_add(1, std::memory_order_acq_rel);
    }
    return result;
}

bool IsScanCodePressed(
    const std::array<uint64_t, kKeyboardWordCount>& words,
    uint8_t scan_code) noexcept
{
    return (words[scan_code / 64] & (uint64_t{1} << (scan_code % 64))) != 0;
}

bool AreModifiersPressed(
    const std::array<uint64_t, kKeyboardWordCount>& words,
    uint8_t modifiers) noexcept
{
    constexpr uint8_t kModifierControl = 1u << 0;
    constexpr uint8_t kModifierShift = 1u << 1;
    constexpr uint8_t kModifierAlt = 1u << 2;
    constexpr uint8_t kModifierMask =
        kModifierControl | kModifierShift | kModifierAlt;
    if ((modifiers & ~kModifierMask) != 0)
        return false;
    const bool control = IsScanCodePressed(words, DIK_LCONTROL) ||
        IsScanCodePressed(words, DIK_RCONTROL);
    const bool shift = IsScanCodePressed(words, DIK_LSHIFT) ||
        IsScanCodePressed(words, DIK_RSHIFT);
    const bool alt = IsScanCodePressed(words, DIK_LMENU) ||
        IsScanCodePressed(words, DIK_RMENU);
    return ((modifiers & kModifierControl) == 0 || control) &&
        ((modifiers & kModifierShift) == 0 || shift) &&
        ((modifiers & kModifierAlt) == 0 || alt);
}

bool HasPressedKeyboardKey(const uint8_t* state, DWORD data_size) noexcept
{
    if (state == nullptr)
        return false;
    for (DWORD index = 0; index < data_size; ++index)
        if ((state[index] & 0x80) != 0)
            return true;
    return false;
}

bool HasPressedMouseButton(const uint8_t* state, DWORD data_size) noexcept
{
    constexpr size_t button_offset = offsetof(DIMOUSESTATE2, rgbButtons);
    if (state == nullptr || data_size <= button_offset)
        return false;
    const size_t button_count = std::min<size_t>(
        data_size - button_offset,
        sizeof(DIMOUSESTATE2{}.rgbButtons));
    for (size_t index = 0; index < button_count; ++index)
        if ((state[button_offset + index] & 0x80) != 0)
            return true;
    return false;
}

void ApplySelectiveKeyboardSuppression(
    uint8_t* state,
    DWORD data_size,
    uint32_t policy,
    const std::array<uint64_t, kKeyboardWordCount>& raw_words) noexcept
{
    std::array<uint64_t, kKeyboardWordCount> drain{};
    for (size_t word = 0; word < kKeyboardWordCount; ++word)
    {
        drain[word] = g_keyboard_drain_words[word].load(std::memory_order_acquire) &
            raw_words[word];
    }

    const uint32_t binding_count = std::min<uint32_t>(
        g_hotkey_binding_count.load(std::memory_order_acquire),
        static_cast<uint32_t>(kMaximumHotkeyBindings));
    for (uint32_t index = 0; index < binding_count; ++index)
    {
        const uint32_t packed = g_hotkey_bindings[index].load(std::memory_order_acquire);
        const auto scan_code = static_cast<uint8_t>(packed & 0xFF);
        const auto modifiers = static_cast<uint8_t>((packed >> 8) & 0xFF);
        const auto policy_flag = static_cast<uint8_t>((packed >> 16) & 0xFF);
        if (scan_code == 0 || (policy & policy_flag) == 0 ||
            !IsScanCodePressed(raw_words, scan_code) ||
            !AreModifiersPressed(raw_words, modifiers))
        {
            continue;
        }
        drain[scan_code / 64] |= uint64_t{1} << (scan_code % 64);
    }

    for (size_t word = 0; word < kKeyboardWordCount; ++word)
    {
        g_keyboard_drain_words[word].store(drain[word], std::memory_order_release);
        uint64_t remaining = drain[word];
        while (remaining != 0)
        {
            unsigned long bit = 0;
            _BitScanForward64(&bit, remaining);
            const size_t scan_code = word * 64 + bit;
            if (scan_code < data_size)
                state[scan_code] = 0;
            remaining &= remaining - 1;
        }
    }
}

HRESULT ProcessGetDeviceState(
    SafetyHookInline& hook,
    void* device,
    DWORD data_size,
    void* data) noexcept
{
    const HRESULT result = InvokeGetDeviceStateSafely(hook, device, data_size, data);
    if (FAILED(result) || data == nullptr || data_size == 0)
        return result;

    const uintptr_t device_address = reinterpret_cast<uintptr_t>(device);
    const bool is_keyboard =
        device_address == g_keyboard_device.load(std::memory_order_acquire);
    const bool is_mouse =
        device_address == g_mouse_device.load(std::memory_order_acquire);
    if (!is_keyboard && !is_mouse)
        return result;

    auto* state = static_cast<uint8_t*>(data);
    const bool active = g_active.load(std::memory_order_acquire);
    const uint32_t policy = active
        ? g_policy_flags.load(std::memory_order_acquire)
        : 0;

    if (is_keyboard)
    {
        const auto raw_words = ReadKeyboardWords(state, data_size);
        for (size_t word = 0; word < kKeyboardWordCount; ++word)
            g_keyboard_words[word].store(raw_words[word], std::memory_order_release);
        g_input_sequence.fetch_add(1, std::memory_order_acq_rel);

        const bool capture = (policy & kPolicyCaptureKeyboard) != 0;
        if (capture)
        {
            g_keyboard_drain.store(true, std::memory_order_release);
            std::memset(state, 0, data_size);
        }
        else if (g_keyboard_drain.load(std::memory_order_acquire))
        {
            if (HasPressedKeyboardKey(state, data_size))
                std::memset(state, 0, data_size);
            else
                g_keyboard_drain.store(false, std::memory_order_release);
        }
        ApplySelectiveKeyboardSuppression(state, data_size, policy, raw_words);
        return result;
    }

    const bool capture = (policy & kPolicyCaptureMouse) != 0;
    if (capture)
    {
        g_mouse_drain.store(true, std::memory_order_release);
        std::memset(state, 0, data_size);
    }
    else if (g_mouse_drain.load(std::memory_order_acquire))
    {
        if (HasPressedMouseButton(state, data_size))
            std::memset(state, 0, data_size);
        else
            g_mouse_drain.store(false, std::memory_order_release);
    }
    return result;
}

HRESULT ProcessGetDeviceData(
    SafetyHookInline& hook,
    void* device,
    DWORD object_data_size,
    void* object_data,
    DWORD* object_count,
    DWORD flags) noexcept
{
    const uintptr_t device_address = reinterpret_cast<uintptr_t>(device);
    const bool is_keyboard =
        device_address == g_keyboard_device.load(std::memory_order_acquire);
    const bool is_mouse =
        device_address == g_mouse_device.load(std::memory_order_acquire);
    const uint32_t policy = g_active.load(std::memory_order_acquire)
        ? g_policy_flags.load(std::memory_order_acquire)
        : 0;
    const bool discard =
        (is_keyboard &&
         (((policy & kPolicyCaptureKeyboard) != 0) ||
          g_keyboard_drain.load(std::memory_order_acquire))) ||
        (is_mouse &&
         (((policy & kPolicyCaptureMouse) != 0) ||
          g_mouse_drain.load(std::memory_order_acquire)));
    const DWORD forwarded_flags = discard && object_data != nullptr && object_count != nullptr
        ? flags & ~DIGDD_PEEK
        : flags;
    const HRESULT result = InvokeGetDeviceDataSafely(
        hook,
        device,
        object_data_size,
        object_data,
        object_count,
        forwarded_flags);
    if (SUCCEEDED(result) && object_count != nullptr && discard)
    {
        *object_count = 0;
        return DI_OK;
    }
    return result;
}

HRESULT __fastcall DirectInputGetDeviceStateDetour(
    void* device,
    DWORD data_size,
    void* data)
{
    return ProcessGetDeviceState(g_get_state_hook, device, data_size, data);
}

HRESULT __fastcall DirectInputGetDeviceStateDetourSecondary(
    void* device,
    DWORD data_size,
    void* data)
{
    return ProcessGetDeviceState(g_get_state_hook_secondary, device, data_size, data);
}

HRESULT __fastcall DirectInputGetDeviceDataDetour(
    void* device,
    DWORD object_data_size,
    void* object_data,
    DWORD* object_count,
    DWORD flags)
{
    return ProcessGetDeviceData(
        g_get_data_hook,
        device,
        object_data_size,
        object_data,
        object_count,
        flags);
}

HRESULT __fastcall DirectInputGetDeviceDataDetourSecondary(
    void* device,
    DWORD object_data_size,
    void* object_data,
    DWORD* object_count,
    DWORD flags)
{
    return ProcessGetDeviceData(
        g_get_data_hook_secondary,
        device,
        object_data_size,
        object_data,
        object_count,
        flags);
}

bool TryInstallDeviceHooks(void* device) noexcept
{
    if (device == nullptr)
        return false;

    try
    {
        std::scoped_lock lock(g_hook_mutex);
        uintptr_t vtable = 0;
        uintptr_t get_state = 0;
        uintptr_t get_data = 0;
        if (!SafeReadPointer(reinterpret_cast<uintptr_t>(device), vtable) || vtable == 0 ||
            !SafeReadPointer(
                vtable + sizeof(uintptr_t) * kGetDeviceStateVtableIndex,
                get_state) ||
            !SafeReadPointer(
                vtable + sizeof(uintptr_t) * kGetDeviceDataVtableIndex,
                get_data) ||
            !IsExecutableAddress(get_state) ||
            !IsExecutableAddress(get_data))
        {
            return false;
        }

        bool state_ready = false;
        if (get_state == g_get_state_target && g_get_state_hook)
            state_ready = true;
        else if (get_state == g_get_state_target_secondary && g_get_state_hook_secondary)
            state_ready = true;
        else if (g_get_state_target == 0)
        {
            if (InstallInlineHook(
                    g_get_state_hook,
                    get_state,
                    reinterpret_cast<void*>(&DirectInputGetDeviceStateDetour)))
            {
                g_get_state_target = get_state;
                state_ready = true;
            }
        }
        else if (g_get_state_target_secondary == 0)
        {
            if (InstallInlineHook(
                    g_get_state_hook_secondary,
                    get_state,
                    reinterpret_cast<void*>(&DirectInputGetDeviceStateDetourSecondary)))
            {
                g_get_state_target_secondary = get_state;
                state_ready = true;
            }
        }

        bool data_ready = false;
        if (get_data == g_get_data_target && g_get_data_hook)
            data_ready = true;
        else if (get_data == g_get_data_target_secondary && g_get_data_hook_secondary)
            data_ready = true;
        else if (g_get_data_target == 0)
        {
            if (InstallInlineHook(
                    g_get_data_hook,
                    get_data,
                    reinterpret_cast<void*>(&DirectInputGetDeviceDataDetour)))
            {
                g_get_data_target = get_data;
                data_ready = true;
            }
        }
        else if (g_get_data_target_secondary == 0)
        {
            if (InstallInlineHook(
                    g_get_data_hook_secondary,
                    get_data,
                    reinterpret_cast<void*>(&DirectInputGetDeviceDataDetourSecondary)))
            {
                g_get_data_target_secondary = get_data;
                data_ready = true;
            }
        }

        return state_ready && data_ready;
    }
    catch (...)
    {
        return false;
    }
}

void RegisterDirectInputDevice(const GUID* device_guid, void* device) noexcept
{
    if (device_guid == nullptr || device == nullptr)
    {
        return;
    }

    if (IsEqualGUID(*device_guid, kDirectInputSystemKeyboard))
    {
        g_keyboard_device.store(
            reinterpret_cast<uintptr_t>(device),
            std::memory_order_release);
        if (TryInstallDeviceHooks(device))
            g_ready_flags.fetch_or(kReadyKeyboard, std::memory_order_acq_rel);
        return;
    }

    if (IsEqualGUID(*device_guid, kDirectInputSystemMouse))
    {
        g_mouse_device.store(
            reinterpret_cast<uintptr_t>(device),
            std::memory_order_release);
        if (TryInstallDeviceHooks(device))
            g_ready_flags.fetch_or(kReadyMouse, std::memory_order_acq_rel);
    }
}

HRESULT __fastcall DirectInputCreateDeviceDetour(
    void* factory,
    const GUID* device_guid,
    void** output_device,
    void* outer)
{
    HRESULT result = DIERR_GENERIC;
    try
    {
        result = InvokeCreateDeviceSafely(
            factory,
            device_guid,
            output_device,
            outer);
    }
    catch (...)
    {
        return DIERR_GENERIC;
    }

    if (SUCCEEDED(result) && output_device != nullptr && *output_device != nullptr)
        RegisterDirectInputDevice(device_guid, *output_device);
    return result;
}

void TryInstallFactoryHook(void* factory) noexcept
{
    if (factory == nullptr)
        return;

    try
    {
        std::scoped_lock lock(g_hook_mutex);
        if (g_create_device_hook)
        {
            g_ready_flags.fetch_or(kReadyFactory, std::memory_order_acq_rel);
            return;
        }

        uintptr_t vtable = 0;
        uintptr_t create_device = 0;
        if (!SafeReadPointer(reinterpret_cast<uintptr_t>(factory), vtable) || vtable == 0 ||
            !SafeReadPointer(
                vtable + sizeof(uintptr_t) * kCreateDeviceVtableIndex,
                create_device) ||
            !IsExecutableAddress(create_device))
        {
            return;
        }

        if (InstallInlineHook(
                g_create_device_hook,
                create_device,
                reinterpret_cast<void*>(&DirectInputCreateDeviceDetour)))
            g_ready_flags.fetch_or(kReadyFactory, std::memory_order_acq_rel);
    }
    catch (...)
    {
        // DirectInput creation succeeded; optional device observation failed closed.
    }
}

HRESULT WINAPI DirectInput8CreateDetour(
    HINSTANCE instance,
    DWORD version,
    REFIID interface_id,
    LPVOID* output,
    LPUNKNOWN outer)
{
    HRESULT result = DIERR_GENERIC;
    try
    {
        result = InvokeDirectInput8CreateSafely(
            instance,
            version,
            interface_id,
            output,
            outer);
    }
    catch (...)
    {
        return DIERR_GENERIC;
    }

    const bool is_direct_input_8 =
        IsEqualGUID(interface_id, kDirectInput8AInterface) ||
        IsEqualGUID(interface_id, kDirectInput8WInterface);
    if (SUCCEEDED(result) && is_direct_input_8 && output != nullptr && *output != nullptr)
        TryInstallFactoryHook(*output);
    return result;
}

void TryInstallXInputObserver() noexcept
{
    constexpr const char* modules[] = {
        "XINPUT1_4.dll",
        "XINPUT1_3.dll",
        "XINPUT9_1_0.dll",
        "XINPUTUAP.dll",
    };
    for (const char* module : modules)
    {
        if (PatchMainModuleImport(
                module,
                "XInputGetState",
                reinterpret_cast<void*>(&XInputGetStateDetour),
                g_original_xinput_get_state))
        {
            g_ready_flags.fetch_or(kReadyXInput, std::memory_order_acq_rel);
            return;
        }
    }
}

bool InstallBroker() noexcept
{
    std::scoped_lock lock(g_hook_mutex);
    if ((g_ready_flags.load(std::memory_order_acquire) & kReadyIat) != 0)
        return true;

    if (!PatchMainModuleImport(
            "DINPUT8.dll",
            "DirectInput8Create",
            reinterpret_cast<void*>(&DirectInput8CreateDetour),
            g_original_direct_input8_create))
    {
        return false;
    }

    g_ready_flags.fetch_or(kReadyIat, std::memory_order_acq_rel);
    TryInstallXInputObserver();
    g_active.store(true, std::memory_order_release);
    return true;
}

}

extern "C" __declspec(dllexport) int32_t __cdecl
GBFRChatOverlay_InstallDirectInputBroker()
{
    return InstallBroker() ? 1 : 0;
}

extern "C" __declspec(dllexport) int32_t __cdecl
GBFRChatOverlay_SetDirectInputBrokerActive(int32_t requested)
{
    const bool active = requested != 0 &&
        (g_ready_flags.load(std::memory_order_acquire) & kReadyIat) != 0;
    g_active.store(active, std::memory_order_release);
    if (!active)
    {
        g_policy_flags.store(0, std::memory_order_release);
        for (size_t word = 0; word < kKeyboardWordCount; ++word)
        {
            g_keyboard_drain_words[word].store(0, std::memory_order_release);
            g_keyboard_words[word].store(0, std::memory_order_release);
        }
        g_keyboard_drain.store(false, std::memory_order_release);
        g_mouse_drain.store(false, std::memory_order_release);
        for (size_t index = 0; index < XUSER_MAX_COUNT; ++index)
            g_controller_user_buttons[index].store(0, std::memory_order_release);
        g_controller_buttons.store(0, std::memory_order_release);
        g_input_sequence.fetch_add(1, std::memory_order_acq_rel);
    }
    return active || requested == 0 ? 1 : 0;
}

extern "C" __declspec(dllexport) int32_t __cdecl
GBFRChatOverlay_SetDirectInputPolicy(uint32_t policy_flags)
{
    if ((policy_flags & ~kPolicyMask) != 0 ||
        (g_ready_flags.load(std::memory_order_acquire) & kReadyIat) == 0)
    {
        return 0;
    }
    g_policy_flags.store(policy_flags, std::memory_order_release);
    return 1;
}

extern "C" __declspec(dllexport) int32_t __cdecl
GBFRChatOverlay_SetDirectInputHotkeyBindings(
    const DirectInputHotkeyBinding* bindings,
    uint32_t binding_count)
{
    if (binding_count > kMaximumHotkeyBindings ||
        (binding_count != 0 && bindings == nullptr))
    {
        return 0;
    }

    constexpr uint8_t kModifierMask = 0x07;
    constexpr uint8_t kAllowedPolicyFlags =
        static_cast<uint8_t>(
            kPolicySuppressActivation |
            kPolicySuppressSettings |
            kPolicySuppressPushToTalk |
            kPolicySuppressQuickActions);
    std::array<uint32_t, kMaximumHotkeyBindings> validated{};
    __try
    {
        for (uint32_t index = 0; index < binding_count; ++index)
        {
            const DirectInputHotkeyBinding& binding = bindings[index];
            if (binding.scan_code == 0 || binding.reserved != 0 ||
                (binding.modifiers & ~kModifierMask) != 0 ||
                binding.policy_flag == 0 ||
                (binding.policy_flag & ~kAllowedPolicyFlags) != 0 ||
                (binding.policy_flag & (binding.policy_flag - 1)) != 0)
            {
                return 0;
            }
            validated[index] = binding.scan_code |
                (static_cast<uint32_t>(binding.modifiers) << 8) |
                (static_cast<uint32_t>(binding.policy_flag) << 16);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }

    g_hotkey_binding_count.store(0, std::memory_order_release);
    for (size_t index = 0; index < kMaximumHotkeyBindings; ++index)
        g_hotkey_bindings[index].store(validated[index], std::memory_order_release);
    g_hotkey_binding_count.store(binding_count, std::memory_order_release);
    return 1;
}

extern "C" __declspec(dllexport) int32_t __cdecl
GBFRChatOverlay_GetDirectInputSnapshot(
    DirectInputBrokerSnapshot* snapshot,
    uint32_t snapshot_size)
{
    if (snapshot == nullptr || snapshot_size < sizeof(DirectInputBrokerSnapshot))
        return 0;

    DirectInputBrokerSnapshot value{};
    value.abi_version = kBrokerAbiVersion;
    value.struct_size = sizeof(DirectInputBrokerSnapshot);
    value.sequence = g_input_sequence.load(std::memory_order_acquire);
    for (size_t word = 0; word < kKeyboardWordCount; ++word)
        value.keyboard_words[word] = g_keyboard_words[word].load(std::memory_order_acquire);
    value.controller_buttons = g_controller_buttons.load(std::memory_order_acquire);
    value.ready_flags = g_ready_flags.load(std::memory_order_acquire);
    value.policy_flags = g_policy_flags.load(std::memory_order_acquire);
    value.active = g_active.load(std::memory_order_acquire) ? 1u : 0u;
    __try
    {
        *snapshot = value;
        return 1;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }
}
