import struct
import sys
import os

def find_id_in_rsb(filename, target_id):
    try:
        with open(filename, 'rb') as f:
            data = f.read()
        if data[0:3] != b'RSC': return None
        version = data[4]
        num_resources = struct.unpack('<I', data[8:12])[0]
        cursor = 12
        for i in range(num_resources):
            if cursor + 4 > len(data): break
            res_id = struct.unpack('<I', data[cursor:cursor+4])[0]
            cursor += 4
            if version >= 5:
                if cursor + 4 > len(data): break
                cursor += 4
            start = cursor
            while cursor < len(data) and data[cursor] != 0:
                cursor += 1
            if res_id == target_id:
                return data[start:cursor].decode('latin-1')
            cursor += 1
    except: pass
    return None

target = 20055
for f in os.listdir('Resources/strings'):
    if f.endswith('.rsb'):
        path = os.path.join('Resources/strings', f)
        text = find_id_in_rsb(path, target)
        if text:
            print(f"File: {f}, ID {target} = '{text}'")
