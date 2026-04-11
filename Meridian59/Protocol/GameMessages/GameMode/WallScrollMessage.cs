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
using Meridian59.Common.Constants;
using Meridian59.Protocol.Enums;

namespace Meridian59.Protocol.GameMessages
{
    public class WallScrollMessage : GameModeMessage
    {       
        #region IByteSerializable implementation
        public override int ByteLength
        {
            get
            {
                return base.ByteLength + TypeSizes.SHORT + 4 + 4; // wallNum + speedX (float) + speedY (float)
            }
        }

        public override int WriteTo(byte[] Buffer, int StartIndex = 0)
        {
            int cursor = StartIndex;

            cursor += base.WriteTo(Buffer, cursor);
            
            Array.Copy(BitConverter.GetBytes(WallNum), 0, Buffer, cursor, TypeSizes.SHORT);
            cursor += TypeSizes.SHORT;

            Array.Copy(BitConverter.GetBytes(SpeedX), 0, Buffer, cursor, 4);
            cursor += 4;

            Array.Copy(BitConverter.GetBytes(SpeedY), 0, Buffer, cursor, 4);
            cursor += 4;
                  
            return cursor - StartIndex;
        }

        public override int ReadFrom(byte[] Buffer, int StartIndex = 0)
        {
            int cursor = StartIndex;

            cursor += base.ReadFrom(Buffer, cursor);

            WallNum = BitConverter.ToUInt16(Buffer, cursor);
            cursor += TypeSizes.SHORT;

            SpeedX = BitConverter.ToSingle(Buffer, cursor);
            cursor += 4;

            SpeedY = BitConverter.ToSingle(Buffer, cursor);
            cursor += 4;
          
            return cursor - StartIndex;
        }
        #endregion

        public ushort WallNum { get; set; }
        public float SpeedX { get; set; }
        public float SpeedY { get; set; }

        public WallScrollMessage(ushort WallNum, float SpeedX, float SpeedY) 
            : base(MessageTypeGameMode.WallScroll)
        {
            this.WallNum = WallNum;
            this.SpeedX = SpeedX;
            this.SpeedY = SpeedY;
        }

        public WallScrollMessage(byte[] Buffer, int StartIndex = 0) 
            : base (Buffer, StartIndex) { }
    }
}
