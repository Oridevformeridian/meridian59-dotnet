import asyncio
import os
import sys

# Add the tests directory to sys.path so we can import simulator
sys.path.append('/var/home/mycroft/src/Meridian59/tests')

# Ensure we can find libprotocol.so
os.environ['LD_LIBRARY_PATH'] = '/var/home/mycroft/src/Meridian59/tests'

from simulator import MeridianClient

async def main():
    print("Connecting to local server to create character for 'tester'...")
    # Change working directory so simulator can find kodbase.txt etc. if needed
    # though simulator seems to use relative paths like 'tests/libprotocol.so'
    # we might need to adjust.
    
    bot = MeridianClient("127.0.0.1", 5959, "tester", "password", verbose=True)
    # The run() method will handle the protocol and character creation
    # It will stop when it reaches INGAME or errors out.
    # We'll wrap it in a timeout.
    try:
        await asyncio.wait_for(bot.run(), timeout=10.0)
    except asyncio.TimeoutError:
        print("Bot run timed out (this might be okay if it reached world)")
    except Exception as e:
        print(f"Error: {e}")

if __name__ == "__main__":
    asyncio.run(main())
