﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("P3.Driver.MBus.Tests")]

namespace P3.Driver.MBus.Frames.VariableData
{
    public class VariableDataBlock
    {
        public DataInformationField DataInformationField { get; private set; }
        private readonly IList<DataInformationFieldExtension> _difes = new List<DataInformationFieldExtension>();

        public ValueInformationField ValueInformationField { get; private set; }
        private readonly IList<ValueInformationField> _vifes = new List<ValueInformationField>();

        private byte[] _data;

        public byte[] Data => _data;

        public int StorageNumber { get; private set; }
        public int Tariff { get; private set; }

        public object Value { get; set; }

        private VariableDataBlock()
        {
            
        }

        public static VariableDataBlock Parse(ReadOnlySpan<byte> data, int offset, Dictionary<Unit, int> usedStorageMap, out int bytesUsed, ILogger logger)
        {
            var vdb = new VariableDataBlock();
            bytesUsed = vdb.ParseData(data, offset, usedStorageMap, logger);
            return vdb;
        }

        private int ParseData(ReadOnlySpan<byte> data, int offset, Dictionary<Unit, int> usedStorageMap, ILogger logger)
        {
            ReadOnlySpan<byte> dataSpan = data;

            DataInformationField = DataInformationField.Parse(in data[offset]);
            bool hasExtension = DataInformationField.HasExtension;
            int index = offset + 1;

            int storageBitIndex = 1;
            int tariffBitIndex = 0;
            StorageNumber = DataInformationField.LsbStorageNumber ? 1 : 0;

            while (hasExtension)
            {
                var dife = DataInformationFieldExtension.Parse(in data[index++]);

                hasExtension = dife.HasExtension;

                StorageNumber |= dife.StorageNumber << storageBitIndex;
                storageBitIndex += 4;

                Tariff |= dife.Tariff << tariffBitIndex;
                tariffBitIndex += 2;

                _difes.Add(dife);
            }


            if (data.Length > index)
            {
                ValueInformationField = ValueInformationField.Parse(in data[index++]);

                hasExtension = ValueInformationField.HasExtension;

                while (hasExtension)
                {
                    var vife = ValueInformationField.Parse(in data[index++]);

                    hasExtension = vife.HasExtension;

                    _vifes.Add(vife);
                }
            }
            else
            {
                ValueInformationField = ValueInformationField.Default();
            }

            if (ValueInformationField.RawData == 0xFD || ValueInformationField.RawData == 0xFB)
            {
                ValueInformationField.ParseExtensionUnit(_vifes[0]);

            }

            if (DataInformationField.DataFieldType == DataFieldType.SpecialFunction)
            {
                int valueLength = data.Length - index;
                _data = dataSpan.Slice(index, valueLength).ToArray();

                index += valueLength;

            }
            else
            {
                int valueLength = DataInformationField.DataFieldLength;
                _data = dataSpan.Slice(index, valueLength).ToArray();

                index += valueLength;
                var value = ConvertValue();
                if (value != null)
                {
                    if (double.TryParse(value.ToString(), out var dblValue))
                    {
                        if (ValueInformationField.Multiplier > 0)
                        {
                            value = dblValue * Math.Pow(10, ValueInformationField.Multiplier);
                        }
                        else if (ValueInformationField.Multiplier < 0)
                        {
                            value = dblValue * Math.Pow(10, ValueInformationField.Multiplier);
                        }
                    }

                    Value = value;
                }
            }

            if (!usedStorageMap.ContainsKey(ValueInformationField.Unit))
            {
                usedStorageMap.Add(ValueInformationField.Unit, 0);
            }
            else
            {
                var storageIndex = usedStorageMap[ValueInformationField.Unit];
                if (StorageNumber == storageIndex)
                {
                    StorageNumber += 1;
                    usedStorageMap[ValueInformationField.Unit] = storageIndex + 1;
                }

            }

            logger.LogTrace($"{ValueInformationField.Unit} {Value} storage {StorageNumber} tariff {Tariff}");


            return index - offset;
        }

        private object ConvertValue()
        {
            if (_data.Length == 0)
            {
                return null;
            }
            switch (ValueInformationField.Unit)
            {
                case Unit.TimePoint:
                {
                    DateTime? value = null;
                    if (_data.Length == 6 && (_data[1] & 0x80) == 0)
                    {
                        //var isDaylightSaving = (_data[0] & 0x40) > 0;
                        value = new DateTime(2000 + (((_data[3] & 0xE0) >> 5) | ((_data[4] & 0xF0) >> 1)), (_data[4] & 0x0F) , _data[3] & 0x1F,
                            _data[2] & 0x1F, _data[1] & 0x3F, _data[0] & 0x3F);

                    }
                    if (_data.Length == 4 && (_data[1] & 0x80) == 0)
                    {
                        value = new DateTime(2000 + (((_data[2] & 0xE0) >> 5) |
                                                    ((_data[3] & 0xF0) >> 1)), (_data[3] & 0x0F), _data[2] & 0x1F,
                            _data[1] & 0x1F, _data[0] & 0x3F, 0);
                    }
                    if (_data.Length == 2)
                    {
                        value = new DateTime(2000 + (((_data[0] & 0xE0) >> 5) | ((_data[1] & 0xF0) >> 1)), (_data[1] & 0x0F), _data[0] & 0x1F);
                    }

                    return value;

                }
            }
            switch (DataInformationField.DataFieldType)
            {
                case DataFieldType.Integer8Bit:
                    return _data[0];
                case DataFieldType.Integer16Bit:
                    return BitConverter.ToInt16(_data, 0);
                case DataFieldType.Integer24Bit:
                    return Bit24ToInt32(_data);
                case DataFieldType.Integer32Bit:
                    return BitConverter.ToInt32(_data, 0);
                case DataFieldType.Integer48Bit:
                    return Bit48ToInt64(_data);
                case DataFieldType.Integer64Bit:
                    return BitConverter.ToInt64(_data, 0);
                case DataFieldType.Digit2Bcd:
                    return _data[0];
                case DataFieldType.Digit4Bcd:
                    return ConvertBcdValue(1);
                case DataFieldType.Digit6Bcd:
                    return ConvertBcdValue(2);
                case DataFieldType.Digit8Bcd:
                    return ConvertBcdValue(3);
                case DataFieldType.Digit12Bcd:
                    return ConvertBcdValue(4);
                
                case DataFieldType.NoData:
                    return 0;
            }

            throw new NotImplementedException();
        }
        private static int Bit24ToInt32(byte[] byteArray)
        {
            var data = new byte[4];
            data[0] = byteArray[0];
            data[1] = byteArray[1];
            data[2] = byteArray[2];

            var test = BitConverter.ToInt32(data, 0);
            
            return test;
        }
        internal static long Bit48ToInt64(byte[] byteArray)
        {
            Span<byte> byteArrayIn = byteArray;
            Span<byte> byteArraySpan = stackalloc byte[8]; // Using C# 7.2 stackalloc support for spans

            if (BitConverter.IsLittleEndian)
            {
                byteArrayIn.Reverse();
            }

            byteArrayIn.CopyTo(byteArraySpan);

            long myLong = BitConverter.ToInt64(byteArraySpan.ToArray(), 0);

            return myLong;
        }

        private double ConvertBcdValue(int length)
        {
            double val = 0;

            for (int i = length; i > 0; i--)
            {
                val = (val * 10) + ((_data[i - 1] >> 4) & 0xF);
                val = (val * 10) + (_data[i - 1] & 0xF);
            }

            return val;
        }
    }
}
