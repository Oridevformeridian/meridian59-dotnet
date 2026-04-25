import os
import struct

def merge_rsc_files(rsc_dir, output_file):
    resources = {} # ID -> Text
    
    # RSC/RSB signature is 'RSC' + 0x01
    signature = b'RSC\x01'
    version = 4
    
    for f in os.listdir(rsc_dir):
        if f.endswith('.rsc'):
            path = os.path.join(rsc_dir, f)
            try:
                with open(path, 'rb') as file:
                    data = file.read()
                
                if data[0:4] != signature:
                    continue
                
                f_version = data[4]
                num_res = struct.unpack('<I', data[8:12])[0]
                
                cursor = 12
                for _ in range(num_res):
                    if cursor + 4 > len(data): break
                    res_id = struct.unpack('<I', data[cursor:cursor+4])[0]
                    cursor += 4
                    
                    if f_version >= 5:
                        cursor += 4 # Skip language
                    
                    start = cursor
                    while cursor < len(data) and data[cursor] != 0:
                        cursor += 1
                    
                    text = data[start:cursor].decode('latin-1')
                    cursor += 1 # Skip null
                    
                    # Store resource, overwrite duplicates (last one wins)
                    resources[res_id] = text
            except Exception as e:
                print(f"Error processing {f}: {e}")

    # Write merged file
    # Sort resources by ID for consistency
    sorted_ids = sorted(resources.keys())
    
    with open(output_file, 'wb') as f:
        f.write(signature)
        f.write(struct.pack('<I', version))
        f.write(struct.pack('<I', len(sorted_ids)))
        
        for res_id in sorted_ids:
            f.write(struct.pack('<I', res_id))
            f.write(resources[res_id].encode('latin-1'))
            f.write(b'\x00')
            
    print(f"Merged {len(resources)} resources from {rsc_dir} into {output_file}")

if __name__ == "__main__":
    rsc_dir = '../../Meridian59/run/server/rsc/'
    output_file = 'Resources/strings/rsc0000-server.rsb'
    merge_rsc_files(rsc_dir, output_file)
