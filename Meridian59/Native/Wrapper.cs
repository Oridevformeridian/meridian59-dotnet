/*
 Copyright (c) 2012-2013 Clint Banzhaf
 This file is part of "Meridian59 .NET".

 "Meridian59 .NET" is free software: 
 You can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, 
 either version 3 of the License, or (at your option) any later version.

 "Meridian59 .NET" is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
 without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 See the GNU General Public License for more details.

 You should have received a copy of the GNU General Public License along with "Meridian59 .NET".
 If not, see http://www.gnu.org/licenses/.
*/

using System;
using System.Runtime.InteropServices;

namespace Meridian59.Native
{
    /// <summary>
    /// Access functions based on hosting CLR.
    /// Might directly call Windows API on MS .NET
    /// </summary>
    public static class Wrapper
    {
        public static void CopyMem(byte[] Source, int SrcIndex, byte[] Destination, int DestIndex, uint Length)
        {
            Array.Copy(Source, SrcIndex, Destination, DestIndex, Length);
        }

        public static void CopyMem(byte[] Source, int Index, IntPtr Destination, uint Length)
        {
            Marshal.Copy(Source, Index, Destination, (int)Length);
        }

        public static void CopyMem(IntPtr Source, byte[] Destination, int Length)
        {
            Marshal.Copy(Source, Destination, 0, Length);
        }

        public static void CopyMem(IntPtr Source, IntPtr Destination, uint Length)
        {
#if WINCLR
            Windows.Kernel32.RtlMoveMemory(Destination, Source, Length); 
#elif MONO
            Linux.Libc.memcpy(Destination, Source, Length); 
#else
            // Fallback for other CLRs: no fast direct IntPtr to IntPtr copy in standard managed code
            // We could use a loop with Marshal.ReadByte/WriteByte but that's slow.
            // For now, let's assume we are either on Windows or Mono.
            throw new NotImplementedException("CopyMem(IntPtr, IntPtr, uint) not implemented for this CLR.");
#endif
        }
    }
}
