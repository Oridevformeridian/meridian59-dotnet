# Meridian 59 TUI Map Implementation

This document describes the coordinate system and rendering logic used in the Meridian 59 TUI client, matched against the original C++ (`clientd3d`) and .NET reference implementations.

## Coordinate Systems

### 1. World Space (M59 Units)
- **Origin (0,0)**: Far North-West corner of the world.
- **X-axis**: Increases towards the **East**.
- **Y-axis**: Increases towards the **South**.

### 2. ROO Space (Resource Units)
- ROO files use a finer coordinate system.
- **Conversion**: `Roo = (M59 * 16) - 1024`.

### 3. Grid Space (Rows/Columns) - WORLD RELATIVE
- The map is divided into 1024-unit blocks (in ROO space).
- **Coordinate Display (R:, C:)**: These coordinates are **attached to the world map**.
- **Anchor (R:1, C:1)**: Always corresponds to the **North-West corner of the room**, regardless of display rotation.
- **Calculation**:
  1. `Col = ((RooX - RoomMinX) / 1024) + 1`
  2. `Row = ((RooY - RoomMinY) / 1024) + 1`

## TUI Rendering Logic

### View Orientations

The TUI supports three visual orientations.

#### 1. North-Up (Identity)
- **Visual**: North is at the Top, West is at the Left.
- **Mapping**: 
  - Terminal Top = World North (Min Y).
  - Terminal Left = World West (Min X).

#### 2. South-Up (180° Rotation) - DEFAULT
- **Visual**: South is at the Top, East is at the Left.
- **Mapping**:
  - Terminal Top = World South (Max Y).
  - Terminal Left = World East (Max X).
- **Rotation**: Full 180-degree rotation (Flip X, Flip Y).

#### 3. Follow-Rotation (Dynamic)
- **Visual**: The player's facing direction is always "Up" on the terminal.
- **Mapping**: Dynamic rotation matrix based on `avatarAngle`.

### Control Strategy

1.  **Movement**: Directional keys (Up/Down/Left/Right) and WASD are interpreted as **screen-space** movements.
2.  **Rotation**: The client translates screen-space movement vectors into world-space offsets by applying the inverse of the current view rotation.
3.  **Consistency**: Pressing "Up" always moves the character towards the top of the terminal screen, regardless of the map's orientation.
