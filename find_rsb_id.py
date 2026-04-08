import struct
import sys

def find_string_id(filename, target_string):
    try:
        with open(filename, 'rb') as f:
            data = f.read()
            
        # Check header: 0x52 0x53 0x43 (RSC)
        if data[0:3] != b'RSC':
            print(f"Not a valid RSC/RSB file: {filename} (starts with {data[0:3]})")
            return

        version = data[4] # The 5th byte in your hexdump was 05
        num_resources = struct.unpack('<I', data[8:12])[0]
        print(f"File: {filename}, Version: {version}, Resources: {num_resources}")

        cursor = 12
        for i in range(num_resources):
            if cursor + 4 > len(data): break
            res_id = struct.unpack('<I', data[cursor:cursor+4])[0]
            cursor += 4
            
            # Version 5 and above has language code (4 bytes)
            if version >= 5:
                if cursor + 4 > len(data): break
                lang = struct.unpack('<I', data[cursor:cursor+4])[0]
                cursor += 4
            
            # Find null terminator
            start = cursor
            while cursor < len(data) and data[cursor] != 0:
                cursor += 1
            
            try:
                # Use latin-1/1252
                text = data[start:cursor].decode('latin-1')
            except:
                text = ""
                
            if text.lower() == target_string.lower():
                print(f"FOUND: ID={res_id}, Text='{text}'")
            
            cursor += 1 # skip null
            
    except Exception as e:
        print(f"Error processing {filename}: {e}")

if __name__ == "__main__":
    rsb_file = sys.argv[1] if len(sys.argv) > 1 else 'Resources/strings/rsc0000-git.rsb'
    find_string_id(rsb_file, "char.dll")
