## Summary

<!-- What does this PR do and why? 1-3 bullets. -->

- 

---

## Checklist

### Build & Tests
- [ ] `dotnet build --configuration Release` — no errors
- [ ] `dotnet test --configuration Release` — all pass
- [ ] `dotnet format --verify-no-changes` — no formatting violations

### Correctness
- [ ] Game-memory reads (PlayerState, InventoryManager, Lumina) are on the **framework/UI thread**
- [ ] API calls run on a **background thread** (`_thread.RunBackground`)
- [ ] Background results posted back via `_thread.RunOnUi`
- [ ] No silent exception swallowing — errors logged + surfaced via `_chat.PrintError`

### API compatibility (if touching Models.cs / RaidPlannerClient.cs)
- [ ] New DTOs use `[JsonPropertyName("camelCase")]` to match the Python backend
- [ ] Typed-response endpoints use `PostReturnAsync<TBody, TResult>`

### New sync action (if adding to CharacterSyncOverlay)
- [ ] `TrayState.SyncingX` enum value added
- [ ] Menu item disabled while syncing
- [ ] `BuildStatus()` switch arm added
- [ ] `Dispose()` unsubscribes the new `SyncCompleted` event
- [ ] `RefreshStatus()` guard updated

### Manifest / release (if this PR is shipping a new version)
- [ ] `<Version>` bumped in `XIVRaidPlannerPlugin.csproj`
- [ ] `Changelog` updated in `XIVRaidPlannerPlugin.json`
- [ ] `Description` updated if `/xrp` commands changed
- [ ] Tag pushed after merge: `git tag vX.Y.Z && git push origin vX.Y.Z`
