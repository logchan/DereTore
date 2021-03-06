﻿using System;
using System.Collections.Generic;
using System.IO;

namespace DereTore.ACB {
    public partial class UtfTable {

        internal UtfTable(Stream stream, long offset, long size, string acbFileName, bool disposeStream) {
            _acbFileName = acbFileName;
            _stream = stream;
            _offset = offset;
            _size = size;
            _disposeStream = disposeStream;
        }

        internal virtual void Initialize() {
            var stream = _stream;
            var offset = _offset;

            var magic = stream.PeekBytes(offset, 4);
            if (!AcbHelper.AreDataIdentical(magic, UtfSignature)) {
                throw new FormatException($"'@UTF' signature is not found in '{_acbFileName}' at offset 0x{offset.ToString("x8")}.");
            }
            CheckEncryption(magic);
            using (var tableDataStream = GetTableDataStream(stream, offset)) {
                var header = GetUtfHeader(tableDataStream);
                _utfHeader = header;
                _rows = new Dictionary<string, UtfField>[header.RowCount];
                if (header.TableSize > 0) {
                    InitializeUtfSchema(stream, tableDataStream, 0x20);
                }
            }
        }

        protected override void Dispose(bool disposing) {
            if (_disposeStream) {
                try {
                    _stream.Dispose();
                } catch (ObjectDisposedException) {
                }
            }
        }

        private static Dictionary<string, byte> GetKeysForEncryptedUtfTable(byte[] encryptedUtfSignature) {
            var keys = new Dictionary<string, byte>(2);
            var keysFound = false;
            for (var seed = 0; seed <= byte.MaxValue; seed++) {
                if (keysFound) {
                    break;
                }
                if ((encryptedUtfSignature[0] ^ seed) != UtfSignature[0]) {
                    continue;
                }
                for (var increment = 0; increment <= byte.MaxValue; increment++) {
                    if (keysFound) {
                        break;
                    }
                    var m = (byte)(seed * increment);
                    if ((encryptedUtfSignature[1] ^ m) != UtfSignature[1]) {
                        continue;
                    }
                    var t = (byte)increment;
                    for (var j = 2; j < UtfSignature.Length; j++) {
                        m *= t;
                        if ((encryptedUtfSignature[j] ^ m) != UtfSignature[j]) {
                            break;
                        }
                        if (j != UtfSignature.Length - 1) {
                            continue;
                        }
                        keys.Add(LcgSeedKey, (byte)seed);
                        keys.Add(LcgIncrementKey, (byte)increment);
                        keysFound = true;
                    }
                }
            }
            return keys;
        }

        private void CheckEncryption(byte[] magicBytes) {
            if (AcbHelper.AreDataIdentical(magicBytes, UtfSignature)) {
                _isEncrypted = false;
                _utfReader = new UtfReader();
            } else {
                _isEncrypted = true;
                var lcgKeys = GetKeysForEncryptedUtfTable(magicBytes);
                if (lcgKeys.Count != 2) {
                    throw new FormatException($"Unable to decrypt UTF table at offset 0x{_offset.ToString("x8")}");
                } else {
                    _utfReader = new UtfReader(lcgKeys[LcgSeedKey], lcgKeys[LcgIncrementKey], IsEncrypted);
                }
            }
        }

        private Stream GetTableDataStream(Stream stream, long offset) {
            var tableSize = (int)_utfReader.PeekUInt32(stream, offset, 4) + 8;
            if (!IsEncrypted) {
                return AcbHelper.ExtractToNewStream(stream, offset, tableSize);
            }
            // Another reading process. Unlike the one with direct reading, this may encounter UTF table decryption.
            var originalPosition = stream.Position;
            stream.Seek(offset, SeekOrigin.Begin);
            var totalBytesRead = 0;
            var memory = new byte[tableSize];
            var currentIndex = 0;
            do {
                var shouldRead = tableSize - totalBytesRead;
                var buffer = _utfReader.PeekBytes(stream, offset, shouldRead, totalBytesRead);
                Array.Copy(buffer, 0, memory, currentIndex, buffer.Length);
                currentIndex += buffer.Length;
                totalBytesRead += buffer.Length;
            } while (totalBytesRead < tableSize);
            stream.Position = originalPosition;
            var memoryStream = new MemoryStream(memory, false) {
                Capacity = tableSize
            };
            memoryStream.Seek(0, SeekOrigin.Begin);
            return memoryStream;
        }

        private static UtfHeader GetUtfHeader(Stream stream) {
            return GetUtfHeader(stream, stream.Position);
        }

        private static UtfHeader GetUtfHeader(Stream stream, long offset) {
            if (offset != stream.Position) {
                stream.Seek(offset, SeekOrigin.Begin);
            }
            var header = new UtfHeader {
                TableSize = stream.PeekUInt32BE(4),
                Unknown1 = stream.PeekUInt16BE(8),
                PerRowDataOffset = (uint)stream.PeekUInt16BE(10) + 8,
                StringTableOffset = stream.PeekUInt32BE(12) + 8,
                ExtraDataOffset = stream.PeekUInt32BE(16) + 8,
                TableNameOffset = stream.PeekUInt32BE(20),
                FieldCount = stream.PeekUInt16BE(24),
                RowSize = stream.PeekUInt16BE(26),
                RowCount = stream.PeekUInt32BE(28)
            };
            header.TableName = stream.PeekZeroEndedStringAsAscii(header.StringTableOffset + header.TableNameOffset);
            return header;
        }

        private void InitializeUtfSchema(Stream sourceStream, Stream tableDataStream, long schemaOffset) {
            var header = _utfHeader;
            var rows = _rows;
            var baseOffset = _offset;
            for (uint i = 0; i < header.RowCount; i++) {
                var currentOffset = schemaOffset;
                long currentRowBase = header.PerRowDataOffset + header.RowSize * i;
                long currentRowOffset = 0;
                var row = new Dictionary<string, UtfField>();
                rows[i] = row;

                for (var j = 0; j < header.FieldCount; j++) {
                    var field = new UtfField {
                        Type = tableDataStream.PeekByte(currentOffset)
                    };

                    long nameOffset = tableDataStream.PeekInt32BE(currentOffset + 1);
                    field.Name = tableDataStream.PeekZeroEndedStringAsAscii(header.StringTableOffset + nameOffset);

                    var union = new NumericUnion();
                    var constrainedStorage = (ColumnStorage)(field.Type & (byte)ColumnStorage.Mask);
                    var constrainedType = (ColumnType)(field.Type & (byte)ColumnType.Mask);
                    switch (constrainedStorage) {
                        case ColumnStorage.Constant:
                        case ColumnStorage.Constant2:
                            var constantOffset = currentOffset + 5;
                            long dataOffset;
                            switch (constrainedType) {
                                case ColumnType.String:
                                    dataOffset = tableDataStream.PeekInt32BE(constantOffset);
                                    field.StringValue = tableDataStream.PeekZeroEndedStringAsAscii(header.StringTableOffset + dataOffset);
                                    currentOffset += 4;
                                    break;
                                case ColumnType.Int64:
                                    union.S64 = tableDataStream.PeekInt64BE(constantOffset);
                                    currentOffset += 8;
                                    break;
                                case ColumnType.UInt64:
                                    union.U64 = tableDataStream.PeekUInt64BE(constantOffset);
                                    currentOffset += 8;
                                    break;
                                case ColumnType.Data:
                                    dataOffset = tableDataStream.PeekUInt32BE(constantOffset);
                                    long dataSize = tableDataStream.PeekUInt32BE(constantOffset + 4);
                                    field.Offset = baseOffset + header.ExtraDataOffset + dataOffset;
                                    field.Size = dataSize;
                                    // don't think this is encrypted, need to check
                                    field.DataValue = sourceStream.PeekBytes(field.Offset, (int)dataSize);
                                    currentOffset += 8;
                                    break;
                                case ColumnType.Double:
                                    union.R64 = tableDataStream.PeekDoubleBE(constantOffset);
                                    currentOffset += 8;
                                    break;
                                case ColumnType.Single:
                                    union.R32 = tableDataStream.PeekSingleBE(constantOffset);
                                    currentOffset += 4;
                                    break;
                                case ColumnType.Int32:
                                    union.S32 = tableDataStream.PeekInt32BE(constantOffset);
                                    currentOffset += 4;
                                    break;
                                case ColumnType.UInt32:
                                    union.U32 = tableDataStream.PeekUInt32BE(constantOffset);
                                    currentOffset += 4;
                                    break;
                                case ColumnType.Int16:
                                    union.S16 = tableDataStream.PeekInt16BE(constantOffset);
                                    currentOffset += 2;
                                    break;
                                case ColumnType.UInt16:
                                    union.U16 = tableDataStream.PeekUInt16BE(constantOffset);
                                    currentOffset += 2;
                                    break;
                                case ColumnType.SByte:
                                    union.S8 = tableDataStream.PeekSByte(constantOffset);
                                    currentOffset += 1;
                                    break;
                                case ColumnType.Byte:
                                    union.U8 = tableDataStream.PeekByte(constantOffset);
                                    currentOffset += 1;
                                    break;
                                default:
                                    throw new FormatException($"Unknown column type at offset: 0x{currentOffset.ToString("x8")}");
                            }
                            break;
                        case ColumnStorage.PerRow:
                            // read the constant depending on the type
                            long rowDataOffset;
                            switch (constrainedType) {
                                case ColumnType.String:
                                    rowDataOffset = tableDataStream.PeekUInt32BE(currentRowBase + currentRowOffset);
                                    field.StringValue = tableDataStream.PeekZeroEndedStringAsAscii(header.StringTableOffset + rowDataOffset);
                                    currentRowOffset += 4;
                                    break;
                                case ColumnType.Int64:
                                    union.S64 = tableDataStream.PeekInt64BE(currentRowBase + currentRowOffset);
                                    currentRowOffset += 8;
                                    break;
                                case ColumnType.UInt64:
                                    union.U64 = tableDataStream.PeekUInt64BE(currentRowBase + currentRowOffset);
                                    currentRowOffset += 8;
                                    break;
                                case ColumnType.Data:
                                    rowDataOffset = tableDataStream.PeekUInt32BE(currentRowBase + currentRowOffset);
                                    long rowDataSize = tableDataStream.PeekUInt32BE(currentRowBase + currentRowOffset + 4);
                                    field.Offset = baseOffset + header.ExtraDataOffset + rowDataOffset;
                                    field.Size = rowDataSize;
                                    // don't think this is encrypted
                                    field.DataValue = sourceStream.PeekBytes(field.Offset, (int)rowDataSize);
                                    currentRowOffset += 8;
                                    break;
                                case ColumnType.Double:
                                    union.R64 = tableDataStream.PeekDoubleBE(currentRowBase + currentRowOffset);
                                    currentRowOffset += 8;
                                    break;
                                case ColumnType.Single:
                                    union.R32 = tableDataStream.PeekSingleBE(currentRowBase + currentRowOffset);
                                    currentRowOffset += 4;
                                    break;
                                case ColumnType.Int32:
                                    union.S32 = tableDataStream.PeekInt32BE(currentRowBase + currentRowOffset);
                                    currentRowOffset += 4;
                                    break;
                                case ColumnType.UInt32:
                                    union.U32 = tableDataStream.PeekUInt32BE(currentRowBase + currentRowOffset);
                                    currentRowOffset += 4;
                                    break;
                                case ColumnType.Int16:
                                    union.S16 = tableDataStream.PeekInt16BE(currentRowBase + currentRowOffset);
                                    currentRowOffset += 2;
                                    break;
                                case ColumnType.UInt16:
                                    union.U16 = tableDataStream.PeekUInt16BE(currentRowBase + currentRowOffset);
                                    currentRowOffset += 2;
                                    break;
                                case ColumnType.SByte:
                                    union.S8 = tableDataStream.PeekSByte(currentRowBase + currentRowOffset);
                                    currentRowOffset += 1;
                                    break;
                                case ColumnType.Byte:
                                    union.U8 = tableDataStream.PeekByte(currentRowBase + currentRowOffset);
                                    currentRowOffset += 1;
                                    break;
                                default:
                                    throw new FormatException($"Unknown column type at offset: 0x{currentOffset.ToString("x8")}");
                            }
                            field.ConstrainedType = (ColumnType)field.Type;
                            break;
                        default:
                            throw new FormatException($"Unknown column storage at offset: 0x{currentOffset.ToString("x8")}");
                    }
                    // Union polyfill
                    field.ConstrainedType = constrainedType;
                    switch (constrainedType) {
                        case ColumnType.String:
                        case ColumnType.Data:
                            break;
                        default:
                            field.NumericValue = union;
                            break;
                    }
                    row.Add(field.Name, field);
                    currentOffset += 5; //  sizeof(CriField.Type + CriField.NameOffset)
                }
            }
        }

        private object GetFieldValue(int rowIndex, string fieldName) {
            var rows = Rows;
            if (rowIndex >= rows.Length) {
                return null;
            }
            var row = rows[rowIndex];
            return row.ContainsKey(fieldName) ? row[fieldName].GetValue() : null;
        }

        internal T? GetFieldValueAsNumber<T>(int rowIndex, string fieldName) where T : struct {
            return (T?)GetFieldValue(rowIndex, fieldName);
        }

        internal string GetFieldValueAsString(int rowIndex, string fieldName) {
            return (string)GetFieldValue(rowIndex, fieldName);
        }

        internal byte[] GetFieldValueAsData(int rowIndex, string fieldName) {
            return (byte[])GetFieldValue(rowIndex, fieldName);
        }

        internal long? GetFieldOffset(int rowIndex, string fieldName) {
            var rows = Rows;
            if (rowIndex >= rows.Length) {
                return null;
            }
            var row = rows[rowIndex];
            if (row.ContainsKey(fieldName)) {
                return row[fieldName].Offset;
            }
            return null;
        }

        internal long? GetFieldSize(int rowIndex, string fieldName) {
            var rows = Rows;
            if (rowIndex >= rows.Length) {
                return null;
            }
            var row = rows[rowIndex];
            if (row.ContainsKey(fieldName)) {
                return row[fieldName].Size;
            }
            return null;
        }

        internal static readonly byte[] UtfSignature = { 0x40, 0x55, 0x54, 0x46 }; // '@UTF'
        private static readonly string LcgSeedKey = "SEED";
        private static readonly string LcgIncrementKey = "INC";

        private readonly string _acbFileName;
        private readonly Stream _stream;
        private readonly long _offset;
        private readonly long _size;
        private readonly bool _disposeStream;

        private UtfReader _utfReader;
        private bool _isEncrypted;
        private UtfHeader _utfHeader;
        private Dictionary<string, UtfField>[] _rows;

    }
}
