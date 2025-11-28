# Change Plan

## Phase 1: Architecture & Dependencies Cleanup
- [x] **GameService.ApiService**: Remove unnecessary project reference to `GameService.Web`.
- [x] **GameService.Ludo**: Analyze and potentially separate UI components (`LudoAdminPanel`) from backend logic (`LudoEngine`, `LudoRoomService`) to avoid mixing concerns.
- [x] **GameService.ArchitectureTests**: Add stricter tests to ensure no project references exist between layers, not just type usage.

## Phase 2: GameService.ApiService Hardening
- [x] **Security**: Implement Admin Policy in `AdminEndpoints`. Currently commented out.
- [x] **Security**: Review `EconomyService` for potential exploits (e.g. negative amounts are checked, but concurrency handling needs verification).
- [x] **Performance**: Ensure `PublishAot` compatibility. Fix `LudoRoomService` JSON serialization.
- [x] **Cleanup**: Remove dead code and unused usings.

## Phase 3: GameService.Web Overhaul
- [x] **Architecture**: Remove direct `DbContext` usage in `Home.razor` and other pages. Refactor to use `GameAdminService` or a dedicated `PlayerService` client.
- [x] **Security**: Fix "God Mode" coin editing. Currently uses direct DB access. Should use a secure API endpoint with proper authorization.
- [x] **UX/UI**: Improve error handling and feedback.

## Phase 4: GameService.Ludo Refactoring
- [x] **Refactoring**: `LudoRoomService` mixes Redis logic with Game Logic. Consider repository pattern or cleaner separation.
- [x] **SignalR**: Ensure `LudoHub` handles disconnections and state consistency robustly.

## Phase 5: Final Validation
- [ ] Run all tests (Unit, Component, Architecture, Performance).
- [ ] Verify end-to-end flow.
