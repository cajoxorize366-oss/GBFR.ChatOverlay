# Relink 2.0.4 Addresses and Layouts

## Scope and notation

This document records the fixed-build contract used by 0.7.0. Values labelled **RVA** are relative to the loaded base of `granblue_fantasy_relink.exe`; they are not absolute process addresses. Object offsets are relative to a verified live object. Party functions are resolved by export name from the verified `PartyWin.dll`.

Supported executable SHA-256:

```text
f827f3c13caa90b290fab2fe7e28165a80448fde0a3f7a96d79dac6b8343ff2a
```

Every required RVA is validated against its instruction pattern. RIP-relative globals, call targets, vtables, and import thunks are derived and compared with the expected target before use.

## Native chat and identity RVAs

| Symbol | RVA | Role |
| --- | ---: | --- |
| `SendMessage` | `0x009049F0` | Native raw text send hook/original call |
| `RpcMessage` | `0x00B97950` | Incoming room message hook |
| filtered-send callback | `0x00905160` | `sendMessage` WordFilter completion lambda and actual-send handoff |
| filtered-receive callback | `0x009054B0` | `rpcMessage` WordFilter completion lambda and official-UI handoff |
| chat manager instruction | `0x025F633A` | RIP-relative source used to derive manager slot |
| chat manager slot | `0x07C23460` | Current HUD chat manager global |
| sender-slot resolver | `0x006CD520` | Maps opaque member key to actual slot |
| lobby member lookup | `0x003760A0` | Resolves member profile for name lookup |
| lobby member manager slot | `0x07C21AB8` | Manager global derived from callsite |
| party identity manager slot | `0x07C483A8` | Four-member EntityId table manager |
| lobby member lookup callsite | `0x003C81B0` | Pattern/manager derivation evidence |
| party identity callsite | `0x003C773C` | Pattern and member-field evidence |
| local member slot callsite | `0x009035D0` | Authoritative local member key path |
| local member slot call target | `0x006CD520` | Required derived target |
| `SendStamp` | `0x00903660` | Official stamp send |
| `SendFixedPhrase` | `0x009044F0` | Official phrase send |
| `SendEmotion` | `0x009033A0` | Official emotion send |
| `PlayFixedPhrase` | `0x006E3A00` | Local phrase playback |
| `PlayEmotion` | `0x006E2B30` | Local emotion playback |
| `PFLobbyGetOwner` import thunk | `0x049AD680` | Captures authoritative lobby-owner EntityId |

## Chat packet layout

The RPC hook copies `0x1A0` bytes before decoding.

| Field | Offset/size |
| --- | ---: |
| opaque sender ID | `0x18`, 4 bytes |
| message buffer | `0x1C`, `0x160` bytes |
| maximum text payload | `0x15D` UTF-8 bytes |
| message hash | `0x17C`, 4 bytes |
| sender/cue label | `0x180`, `0x18` bytes |
| category | `0x198`, 4 bytes |
| metadata | `0x19C`, 4 bytes |
| raw text hash | `0x887AE0B0` |

## Native WordFilter callback ABI

Both completion callbacks use the Windows x64 ABI:

```text
rcx = callback closure
rdx = unused callback argument
r8  = pointer to NativeStringView { data, length }
```

Filtered-send closure (`0x60` bytes):

| Field | Offset |
| --- | ---: |
| manager | `0x08` |
| local member key | `0x10` |
| category | `0x14` |
| presentation/cue label bytes | `0x18` |
| label length | `0x58` |

Filtered-receive closure (`0x68` bytes):

| Field | Offset |
| --- | ---: |
| manager | `0x08` |
| sender/member key | `0x10` |
| category | `0x14` |
| presentation/cue label bytes | `0x18` |
| label length | `0x58` |
| metadata | `0x60` |

Raw text calls `WordFilterImpl::sanitizeComment`; non-raw messages bypass these two callbacks and continue through the existing native message path. A cache hit may invoke the callback synchronously, while a miss may complete on a worker thread. Closure and string-view pointers are valid only for the active callback and are never retained by managed code.

## Player name and EntityId layouts

Lobby member/profile:

| Field | Offset |
| --- | ---: |
| member profile | `0x5E60` |
| member active flag | `0x5EBC` |
| profile display name | `0x208` |

Party identity manager:

| Field | Offset/value |
| --- | ---: |
| member count | `4` |
| member stride | `0x58` |
| offline member bank | `0x1C128` |
| online member bank | `0x1C288` |
| online bank selector | `0x6CCE8` |
| local member key table | `0x6C828` |
| EntityId string within member | `0x28` |

Native strings use a 32-byte MSVC string object: length at `0x10`, capacity at `0x18`, inline storage when capacity is `0x0F`. Reads are rejected if manager pointer or bank selector changes during the snapshot.

## Party HUD RVAs

| Symbol | RVA |
| --- | ---: |
| lobby party-HUD factory | `0x02590020` |
| lobby party-HUD destructor | `0x025916C0` |
| battle party-HUD factory | `0x026043B0` |
| battle party-HUD destructor | `0x02605810` |
| Full Chain factory | `0x0262ACA0` |
| Full Chain destructor | `0x0262BDD0` |
| UI object query | `0x026193F0` |
| UI manager slot | `0x07C00598` |
| shared HUD factory target | `0x039C98E0` |
| lobby destructor primary target | `0x02590DC0` |
| battle destructor primary target | `0x026053F0` |
| shared HUD destructor target | `0x04712FBC` |
| Full Chain destructor target | `0x00BB5D40` |
| lobby controller vtable | `0x05A50BD8` |
| battle controller vtable | `0x05A60088` |
| Full Chain primary vtable | `0x05A65B98` |
| Full Chain secondary vtable | `0x05A65CB8` |
| Full Chain tertiary vtable | `0x05A65CC8` |

## Party HUD object layout

| Field | Offset/value |
| --- | ---: |
| controller pointer in factory result | `0x18` |
| UI object final transform | `0x120` |
| UI object size | `0x1BC` |
| UI object active flag | `0x1D0` |
| controller visibility state | `0x188` |
| lobby local/member type | `0x340` |
| battle local/member type | `0x1A0` |
| lobby row pointers | `0x1B8`, `0x230` |
| battle HP-row pointers | `0x250`, `0x270` |
| microphone logical size | `72.0` |
| logical right-edge gap | `48.0` |

Only visibility state `2` emits normal anchors. Any nonzero Full Chain state suppresses all anchors.

## PartyWin contract

Supported `PartyWin.dll`:

| Property | Value |
| --- | --- |
| SHA-256 | `3f0c6abbb735d81fa766a105982bda73f1d2c2cf01109fa2e7cf64813a52ce55` |
| file version | `1.10.2509.24002` |
| product version | `1.10.12` |

Lifecycle hooks resolve these exports directly:

- `PartyInitialize`
- `PartyCleanup`
- `PartyNetworkLeaveNetwork`
- `PartyStartProcessingStateChanges`
- `PartyFinishProcessingStateChanges`

Voice and member tracking additionally bind `PartyGetWorkMode`, `PartyDoWork`, local-device, endpoint, ChatControl, mute, permission, device-selection, connect/disconnect, indicator, and error-message exports declared in `Native/Party/PartyNativeApi.cs`. The module is accepted only when every required export is present.

## Input and graphics native contracts

DirectInput broker:

| Contract | Value |
| --- | ---: |
| ABI version | `2` |
| managed/native snapshot size | `64` bytes |
| `IDirectInput8::CreateDevice` vtable index | `3` |
| `IDirectInputDevice8::GetDeviceState` index | `9` |
| `IDirectInputDevice8::GetDeviceData` index | `10` |
| maximum hotkey bindings | `64` |

The bridge patches the game module's `DirectInput8Create` and `XInputGetState` imports. Cursor release patches its `GetCursorPos`, `SetCursorPos`, and `ClipCursor` imports. Present-chain resolution follows at most `32` supported entry jumps and rejects unreadable, non-executable, cyclic, unsupported, or over-depth chains.

OverlayHub uses API version `2` and graphics binding version `1`.
