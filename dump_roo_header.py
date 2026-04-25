import struct
import sys

def dump_roo(file_path):
    with open(file_path, 'rb') as f:
        data = f.read(128)
        
    sig = struct.unpack('<I', data[0:4])[0]
    print(f"Signature: {hex(sig)}")
    
    ver = struct.unpack('<I', data[4:8])[0]
    print(f"Version: {ver}")
    
    # Skip Challenge (4), OffsetClient (4), OffsetServer (4)
    # Total skip = 12
    # But wait, Encryption check:
    # 727: if (*((uint*)Buffer) == ENCRYPTIONFLAG)
    
    ptr = 20 # after server offset
    enc_flag = struct.unpack('<I', data[ptr:ptr+4])[0]
    if enc_flag == 0xFFFFFFFF:
        print("Encryption enabled")
        ptr += 16 # skip enc flag, stream len, expected response
    
    offset_walls = struct.unpack('<I', data[32:36])[0]
    print(f"OffsetWalls: {offset_walls}")
    
    with open(file_path, 'rb') as f:
        f.seek(offset_walls)
        count = struct.unpack('<H', f.read(2))[0]
        print(f"Walls Count: {count}")
        
        for i in range(count):
            wall_data = f.read(24) # Size of RooWall in v15
            if len(wall_data) < 24: break
            
            # Try both int and float
            p1_x_f = struct.unpack('<f', wall_data[0:4])[0]
            p1_y_f = struct.unpack('<f', wall_data[4:8])[0]
            p2_x_f = struct.unpack('<f', wall_data[8:12])[0]
            p2_y_f = struct.unpack('<f', wall_data[12:16])[0]
            
            p1_x_i = struct.unpack('<i', wall_data[0:4])[0]
            p1_y_i = struct.unpack('<i', wall_data[4:8])[0]
            p2_x_i = struct.unpack('<i', wall_data[8:12])[0]
            p2_y_i = struct.unpack('<i', wall_data[12:16])[0]
            
            left_sector = struct.unpack('<H', wall_data[16:18])[0]
            right_sector = struct.unpack('<H', wall_data[18:20])[0]
            
            if left_sector == 0 or right_sector == 0:
                print(f"EXIT WALL {i}: Int({p1_x_i}, {p1_y_i})->({p2_x_i}, {p2_y_i}) | Float({p1_x_f:.2f}, {p1_y_f:.2f})->({p2_x_f:.2f}, {p2_y_f:.2f}) S:{left_sector}->{right_sector}")





if __name__ == "__main__":
    dump_roo(sys.argv[1])
