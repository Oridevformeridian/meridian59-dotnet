# Meridian 59 Protocol Guide: Movement & Coordinates

This document defines the coordinate systems and conversion logic for the Meridian 59 client, based on the original C++ source (`clientd3d`) and the .NET reference implementation.

## 1. Coordinate Systems

### World Space (M59 / Kod Units)
*   **Source**: The server (`blakserv`/`kod`) and the `DataController` / `RoomObject.Position3D` fields.
*   **Resolution**: 64 "fine units" per 1 "big square" (tile).
*   **Range**: Typically 0 to 4096+ (depending on room size).
*   **Logic**: The Northwest corner of a room is usually at coordinate **64** in the server's 1-based internal system.
*   **Data Type**: `ushort` in network messages; `Real` (float/double) in `Position3D`.

### Resource Space (ROO Units)
*   **Source**: `.roo` files and the collision detection system (`RooFile.VerifyMove`).
*   **Resolution**: 1024 "fine units" per 1 "big square" (tile).
*   **Scaling**: 16x higher resolution than World Space.
*   **Offset**: Subtractive offset of 1024 units to align the 1-based server grid to a 0-based resource grid.
*   **Logic**: `ROO = (M59 * 16) - 1024`.

## 2. Unit Conversions

### World (M59) to Resource (ROO)
Used for rendering and collision detection.
```csharp
ROO_X = (M59_X * 16.0f) - 1024.0f;
ROO_Y = (M59_Y * 16.0f) - 1024.0f; // Note: In ROO, Y is the floor plane (Z in 3D)
```
*   **Height (Z to Y)**: Height in ROO space is scaled by 16 but **has no 1024 offset**.
    `ROO_Height = M59_Height * 16.0f`.

### Resource (ROO) to World (M59)
Used for updating the player's position after a successful collision check.
```csharp
M59_X = (ROO_X + 1024.0f) / 16.0f; // Or ROO_X * 0.0625f + 64.0f
M59_Y = (ROO_Y + 1024.0f) / 16.0f;
```

## 3. Movement Protocol (BP_REQ_MOVE / PI 157)
When requesting a move, the client sends coordinates to the server in **Kod units**.

### Payload Structure:
1.  **NewCoordinateY** (ushort): The M59 Y (South-North) position.
2.  **NewCoordinateX** (ushort): The M59 X (West-East) position.
3.  **Speed** (byte): Horizontal speed.
4.  **RoomID** (uint): The destination room.
5.  **Angle** (ushort): The direction units (0-4096).

**Crucial Note**: The original C++ macro `RequestMove` converts client units back to Kod units before sending:
`ToServer(BP_REQ_MOVE, ..., FinenessClientToKod(x) + KOD_FINENESS, ...)`
This corresponds to: `(ROO_X / 16) + 64`.

## 4. Implementation Rules for TUI Client

1.  **Avatar Objects**: Always store and read `Position3D` in M59 (Kod) units.
2.  **Movement Prediction**: 
    *   Clone `Position3D`.
    *   Call `ConvertToROO()` to enter collision space.
    *   Perform `VerifyMove`.
    *   Scale the result delta by `0.0625f` (1/16) and add it to the original World position.
3.  **Initial Position**: Never send a `ReqMove(0,0)` during room entry (PI 130). Wait for `RoomContents` (PI 134) to provide the valid server-side spawn coordinates.
4.  **Renderer**: The `centerX` and `centerY` must use the `* 16 - 1024` conversion to center the camera on the precise high-precision World position.
5.  **TUI Grid**: (R:, C:) coordinates are for display only. `1024` ROO units = 1 Grid Block. `R:1, C:1` is the Northwest corner of the room (`ROO: 0,0`).
