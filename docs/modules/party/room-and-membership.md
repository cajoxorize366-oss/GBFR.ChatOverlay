# Room and Membership

## Scope

The room module observes Relink's existing PlayFab Party lifecycle and turns it into stable room/member transitions for the chat overlay. It does not create, connect, leave, or destroy the gameplay network.

Primary sources:

- `Native/Party/PartyLifecycleProbe.cs`
- `Native/Party/PartyRoomSessionTracker.cs`
- `Native/Party/PartyRoomMemberTracker.cs`
- `Native/Party/PartyStateChangeReader.cs`
- `Native/Party/PartyRoomTransition.cs`
- `Native/Party/PartyMemberTransition.cs`
- `Native/Identity/PartyRoomIdentitySnapshotResolver.cs`

## Room activation

A room is active only after the same network/local-user pair has authenticated successfully and created its local gameplay endpoint. Create-network and connect-network completions establish whether the local user created or joined the network. The overlay uses this authenticated room state as its visibility gate.

The tracker publishes one `Entered` transition when activation becomes coherent. Existing remote members are emitted as baseline transitions so opening the overlay does not announce them as new joins.

## Member tracking

Party endpoint events are correlated by EntityId, not by endpoint pointer alone. A remote user may expose more than one endpoint, so the member remains present until the last endpoint for that EntityId is gone.

```text
EndpointCreated during Party batch
  -> inspect local/remote ownership and EntityId
  -> correlate with coherent Relink four-slot EntityIds
  -> map to remote overlay player 1-3
  -> publish baseline or joined transition after FinishProcessingStateChanges
```

Endpoint destruction first creates a leave candidate. The candidate is published only after the EntityId is absent from both Party's open endpoints and Relink's coherent member table. This avoids false leave/rejoin messages while Party replaces an endpoint.

Native leave reasons map as follows:

| Native reason | Overlay reason |
| --- | --- |
| `0` | Requested / left voluntarily |
| `1` | Disconnected |
| `2` | Kicked |
| `3` | Device lost authentication |
| `4` | Endpoint creation failed |
| other | Unknown |

## Room exit classification

The successful detour of Relink's own `PartyNetworkLeaveNetwork` is the authoritative graceful-leave signal. It is recorded before later endpoint/local-user/network teardown events arrive.

| Evidence | Exit result |
| --- | --- |
| Successful `PartyNetworkLeaveNetwork`, known local or present remote host | `SelfLeft` |
| Successful leave while the previously bound remote owner is missing | `HostDisconnected` |
| Matching `LocalUserKicked` | `Kicked` and overrides a pending graceful leave |
| Endpoint/user/network disappears without a queued successful leave | `NetworkInterrupted` |

If the identity snapshot is already unavailable during a successful explicit leave, the result remains `SelfLeft`; teardown-time loss of metadata is not reclassified as a network interruption. `PartyCleanup` preserves and finalizes any pending transition before clearing native handles.

## Room and host naming

Room identity is resolved from the current lobby owner EntityId and the coherent Relink party table. A local creator is `LocalHost`; a joiner sees a remote owner only when that owner maps uniquely to one member. The owner's verified Relink display name becomes the room name when available.

Ambiguous owner data produces `Unknown`. The overlay omits the host label or owner name in that state rather than assigning the local player by default.

## Presentation

`ChatOverlayPeer.Tick` drains room and member transitions into system messages and a five-second transient room notice. Transition queues survive the short interval between graceful leave and Party cleanup, but are reset on mod suspension to avoid replaying stale membership events after resume.
