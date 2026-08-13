# Voice Indicators

## Scope

Voice indicators combine native Relink party-HUD geometry with PlayFab Party voice state. The overlay draws microphone glyphs in the foreground, but it does not hard-code screen coordinates or infer a player from vertical order alone.

Primary sources:

- `Native/Hud/RelinkPartyHudTracker.cs`
- `Native/Hud/RelinkUiProjection.cs`
- `Native/Party/PartyVoiceIndicatorSnapshot.cs`
- `Overlay/VoiceIndicatorOverlay.cs`
- `Overlay/VoiceOverlayPresentation.cs`

## HUD controller lifecycle

The tracker hooks the factory and destructor pairs for:

- online-lobby party HUD;
- battle party HUD;
- Full Chain illustration controller.

Each factory result is accepted only when its controller vtable equals the verified Relink 2.0.4 vtable. Destructors remove the exact pointer. Stale pointers discovered during rendering are discarded.

## Anchor projection

An anchor is emitted only when the native controller visibility state is `2`, meaning stable and visible. Opening state `1` and closing state `3` are hidden.

For each row the tracker reads the active UI object, native size, and final 4x4 transform. It projects a point 48 logical units beyond the row's right edge into the current ImGui viewport. The icon's logical size follows the same transform and is clamped to 18-64 pixels after projection.

Lobby rows use controller pointer offsets `0x1B8` and `0x230`. Battle rows use the full-width HP-row geometry at `0x250` and `0x270`. The latter remains stable across resolution, aspect ratio, and HUD-scale changes.

## Full Chain masking

Relink leaves party HP rows rendered underneath the Full Chain illustration. Therefore ordinary party-HUD visibility is not enough. While a valid Full Chain controller has any nonzero visibility state, every microphone anchor is suppressed for its opening, visible, and closing lifetime.

If a live Full Chain controller cannot be read for one frame, the module assumes it is blocking. This fail-closed choice prevents glyphs from appearing over the illustration.

## Voice-to-row mapping

The Party module provides established and talking remote EntityIds. The identity resolver provides one coherent four-member EntityId snapshot and the local actual slot. These are converted to remote overlay players 1-3 before presentation.

`VoiceIndicatorOverlay` requires:

- a valid voice snapshot;
- voice state `Ready` or `Speaking`;
- valid anchors from one layout;
- exactly one local row;
- every established voice player to be present in the occupied member set;
- an unambiguous mapping between remote rows and occupied remote players.

When any condition fails, normal indicators disappear instead of being drawn beside the wrong person. Debug builds can expose all valid anchors through the hidden diagnostic setting, but stable Release builds force that option off.

## Presentation

The local icon is shown when the voice session is ready and becomes fully bright while the local microphone is transmitting. Established remote participants receive idle icons; their icons become bright when Party reports them talking. Idle opacity is 70 percent with a muted palette, while speaking opacity is 100 percent.

The chat header uses the same snapshot to show `[语音] <names> 正在使用语音` when remote talkers can be resolved. Otherwise it displays the coarse voice state such as waiting, connecting, ready, transmitting, disconnecting, or muted.
