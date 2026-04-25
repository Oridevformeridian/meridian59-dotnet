import os
import re

target_id = 20055
rsc_dir = '../../Meridian59/run/server/rsc/'

for f in os.listdir(rsc_dir):
    if f.endswith('.rsc'):
        path = os.path.join(rsc_dir, f)
        try:
            with open(path, 'r', encoding='latin-1') as file:
                content = file.read()
                if str(target_id) in content:
                    print(f"Found {target_id} in {path}")
                    # Print the line
                    for line in content.splitlines():
                        if str(target_id) in line:
                            print(line)
        except:
            pass
