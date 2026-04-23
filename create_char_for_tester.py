import asyncio
import os
import sys
import struct
import ctypes

# Paths
M59_DIR = '/home/mycroft/src/Meridian59'
TESTS_DIR = os.path.join(M59_DIR, 'tests')

# Add to path
sys.path.append(TESTS_DIR)
os.environ['LD_LIBRARY_PATH'] = TESTS_DIR

from simulator import MeridianClient, lib, m59_hash, AP_GETLOGIN, AP_LOGIN, AP_LOGINOK, AP_GETCHOICE, AP_CHOICE, AP_GAME, BP_LOAD_MODULE, BP_SEND_CHARACTERS, BP_CHARACTERS, BP_SYSTEM, SYS_NEW_CHARINFO, BP_USE_CHARACTER, FACE_ICONS

class CharCreator(MeridianClient):
    async def handle_message(self, msg_type, data, seq):
        if seq != 0: self.epoch = seq
        
        print(f"DEBUG: Received msg_type={msg_type} state={self.state}")

        if msg_type == AP_GETLOGIN and self.state == "LOGIN":
            print("Sending login...")
            payload = bytes([AP_LOGIN, 4, 4]) + struct.pack('<IIIIIHHIII', 2, 10, 0, 16*1024*1024, 586, 1024, 768, 1, 1000000, 0) + \
                      struct.pack('<H', len(self.username)) + self.username.encode() + \
                      struct.pack('<H', 16) + m59_hash(self.password)
            await self.send_packet(payload)
            self.encryption_enabled = True
            self.secure_token.value = 0
        
        elif msg_type == AP_LOGINOK and self.state == "LOGIN":
            print("Login OK, selecting character slot...")
            self.state = "CHARSELECT"

        elif msg_type == AP_GETCHOICE and self.state == "CHARSELECT":
            if len(data) >= 21:
                s_vals = struct.unpack('<IIIII', data[1:21])
                for i in range(5): self.seeds[i] = s_vals[i]
            self.secure_token.value = 0 
            await self.send_packet(bytes([AP_CHOICE, 0, 0, 0, 0, 0, 0, 0, 0]) + struct.pack('<H', len(self.username)) + self.username.encode())
        
        elif msg_type == AP_GAME:
            print("Entering game mode...")
            if self.state != "INGAME": self.state = "WAIT_CHARACTERS"
        
        elif msg_type == BP_LOAD_MODULE:
            if self.state == "WAIT_CHARACTERS" or self.state == "INGAME":
                print("Sending request for characters...")
                await self.send_packet(bytes([BP_SEND_CHARACTERS]), use_security=True)
        
        elif msg_type == BP_CHARACTERS:
            print("Received character list.")
            num_chars = struct.unpack('<H', data[1:3])[0]
            if num_chars == 0 or (num_chars > 0 and data[9 + struct.unpack('<H', data[7:9])[0]] != 0):
                # Need to create!
                print("No character found or first-time login. Creating character 'Tester'...")
                cid = 0
                if num_chars > 0:
                    cid = struct.unpack('<I', data[3:7])[0]
                
                char_name = "Tester"
                new_char = bytes([BP_SYSTEM, SYS_NEW_CHARINFO]) + struct.pack('<I', cid) + \
                           struct.pack('<H', len(char_name)) + char_name.encode() + \
                           struct.pack('<H', 4) + b"Bot." + bytes([1]) + \
                           struct.pack('<H', 5) + struct.pack('<IIIII', *FACE_ICONS) + \
                           bytes([0x0C, 0x04]) + \
                           struct.pack('<H', 6) + struct.pack('<IIIIII', 30, 30, 30, 30, 40, 40) + \
                           struct.pack('<HH', 0, 0)
                self.state = "CREATING"
                await self.send_packet(new_char, use_security=True)
            else:
                print("Character already exists and is configured. Done!")
                self.fully_loaded = True
                return False # Stop
        
        elif self.state == "CREATING" and msg_type == BP_SYSTEM:
            # Check for success
            print("Character created successfully!")
            self.fully_loaded = True
            return False

        return True

async def main():
    # We need to run from M59_DIR to find kodbase.txt etc.
    os.chdir(M59_DIR)
    client = CharCreator("138.197.44.253", 5959, "tester", "password", verbose=True)
    try:
        await asyncio.wait_for(client.run(), timeout=15.0)
    except Exception as e:
        print(f"Finished with: {e}")

if __name__ == "__main__":
    asyncio.run(main())
