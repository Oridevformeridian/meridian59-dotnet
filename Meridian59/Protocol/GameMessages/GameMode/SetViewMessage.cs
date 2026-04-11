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
    public class SetViewMessage : GameModeMessage
    {       
        #region IByteSerializable implementation
        public override int ByteLength
        {
            get
            {
                return base.ByteLength + TypeSizes.INT + TypeSizes.INT + TypeSizes.INT + TypeSizes.BYTE;
            }
        }

        public override int WriteTo(byte[] Buffer, int StartIndex = 0)
        {
            int cursor = StartIndex;

            cursor += base.WriteTo(Buffer, cursor);
            
            Array.Copy(BitConverter.GetBytes(ObjectID), 0, Buffer, cursor, TypeSizes.INT);
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(ViewFlags), 0, Buffer, cursor, TypeSizes.INT);
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(ViewHeight), 0, Buffer, cursor, TypeSizes.INT);
            cursor += TypeSizes.INT;

            Buffer[cursor] = ViewLight;
            cursor++;
                  
            return cursor - StartIndex;
        }

        public override int ReadFrom(byte[] Buffer, int StartIndex = 0)
        {
            int cursor = StartIndex;

            cursor += base.ReadFrom(Buffer, cursor);

            ObjectID = BitConverter.ToUInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            ViewFlags = BitConverter.ToInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            ViewHeight = BitConverter.ToInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            ViewLight = Buffer[cursor];
            cursor++;
          
            return cursor - StartIndex;
        }
        #endregion

        public uint ObjectID { get; set; }
        public int ViewFlags { get; set; }
        public int ViewHeight { get; set; }
        public byte ViewLight { get; set; }

        public SetViewMessage(uint ObjectID, int ViewFlags, int ViewHeight, byte ViewLight) 
            : base(MessageTypeGameMode.SetView)
        {
            this.ObjectID = ObjectID;
            this.ViewFlags = ViewFlags;
            this.ViewHeight = ViewHeight;
            this.ViewLight = ViewLight;
        }

        public SetViewMessage(byte[] Buffer, int StartIndex = 0) 
            : base (Buffer, StartIndex) { }
    }
}
