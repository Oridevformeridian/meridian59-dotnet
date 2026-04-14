# Project Mandates

## TUI Client Development & Testing
- **Scriptable Commands**: For every new functional change or feature added to the TUI Client, an appropriate scriptable command MUST be implemented to allow for automated execution.
- **Unit Test Scripts**: Every change or bug fix MUST be accompanied by a `.script` unit test file that exercises the new logic.
- **Verification**: After each change, the TUI Client should be run with the `-s` flag passing the new unit test script, and the resulting logs/behavior must be verified.
- **Continuous Integration**: After a successful build and verified test run, all changes MUST be committed and pushed to the repository.

## Instrumentation & Load Testing
- **Action Recording**: Every player action and client response MUST be recordable by the playback/recorder feature to ensure full session replay capability.
- **Metrics Gathering**: Every message sent and received MUST be recorded with high-precision timestamps to allow for response time metrics gathering, categorized by message type (PI).
