﻿//
// Copyright (c) 2008, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System.Collections.Generic;
using System.IO;

using DirectoryEntry = DiscUtils.Ntfs.IndexEntry<DiscUtils.Ntfs.FileNameRecord, DiscUtils.Ntfs.FileReference>;

namespace DiscUtils.Ntfs
{
    internal class Directory : File
    {
        private DirectoryEntry _rootEntry;
        private Stream _indexStream;

        public Directory(NtfsFileSystem fileSystem, FileRecord baseRecord)
            : base(fileSystem, baseRecord)
        {
            IndexRootFileAttribute indexRoot = (IndexRootFileAttribute)GetAttribute(AttributeType.IndexRoot, "$I30");
            using (Stream s = indexRoot.Open())
            {
                byte[] buffer = Utilities.ReadFully(s, (int)indexRoot.Length);
                _rootEntry = new DirectoryEntry(buffer, (int)indexRoot.Header.OffsetToFirstEntry + 0x10);
            }

            FileAttribute indexAlloc = GetAttribute(AttributeType.IndexAllocation, "$I30");
            if (indexAlloc != null)
            {
                _indexStream = indexAlloc.Open();
            }
        }

        public IEnumerable<File> GetMembers()
        {
            List<DirectoryEntry> entries;
            if ((_rootEntry.Flags & (IndexEntryFlags.End | IndexEntryFlags.Node)) != (IndexEntryFlags.End | IndexEntryFlags.Node))
            {
                entries = EnumerateResident();
            }
            else
            {
                entries = new List<DirectoryEntry>();
                Enumerate(_rootEntry, entries);
            }

            // Weed out short-name versions of files where there's a long name
            Dictionary<FileReference, DirectoryEntry> byRefIndex = new Dictionary<FileReference, DirectoryEntry>();
            int i = 0;
            while(i < entries.Count)
            {
                DirectoryEntry entry = entries[i];

                if (((entry.Key.Flags & (uint)FileAttributes.Hidden) != 0) && _fileSystem.Options.HideHiddenFiles)
                {
                    entries.RemoveAt(i);
                }
                else if (((entry.Key.Flags & (uint)FileAttributes.System) != 0) && _fileSystem.Options.HideSystemFiles)
                {
                    entries.RemoveAt(i);
                }
                else if (entry.Data.MftIndex < 24 && _fileSystem.Options.HideMetafiles)
                {
                    entries.RemoveAt(i);
                }
                else if (byRefIndex.ContainsKey(entry.Data))
                {
                    DirectoryEntry storedEntry = byRefIndex[entry.Data];
                    if (Utilities.Is8Dot3(storedEntry.Key.FileName))
                    {
                        // Make this the definitive entry for the file
                        byRefIndex[entry.Data] = entry;

                        // Remove the old one from the 'entries' array.
                        for (int j = i - 1; j >= 0; --j)
                        {
                            if (entries[j].Data == entry.Data)
                            {
                                entries.RemoveAt(j);
                            }
                        }
                    }
                    else
                    {
                        // Remove this entry
                        entries.RemoveAt(i);
                    }
                }
                else
                {
                    // Only increment if there's no collision - if there was one
                    // we'll have removed an earlier entry in the array, effectively
                    // moving us on one.
                    byRefIndex.Add(entry.Data, entry);
                    ++i;
                }
            }

            return Utilities.Map<DirectoryEntry, File>(entries, (r) => _fileSystem.MasterFileTable.GetFileOrDirectory(r.Data));
        }

        private List<DirectoryEntry> EnumerateResident()
        {
            List<DirectoryEntry> residentEntries = new List<DirectoryEntry>();

            IndexRootFileAttribute indexRoot = (IndexRootFileAttribute)GetAttribute(AttributeType.IndexRoot, "$I30");
            using (Stream s = indexRoot.Open())
            {
                byte[] buffer = Utilities.ReadFully(s, (int)indexRoot.Length);

                long pos = 0;
                while (pos < indexRoot.Header.TotalSizeOfEntries)
                {
                    DirectoryEntry entry = new DirectoryEntry(buffer, (int)(0x10 + indexRoot.Header.OffsetToFirstEntry + pos));
                    if ((entry.Flags & IndexEntryFlags.End) != 0)
                    {
                        break;
                    }

                    residentEntries.Add(entry);

                    pos += entry.Length;
                }
            }

            List<DirectoryEntry> result = new List<DirectoryEntry>();
            foreach (DirectoryEntry entry in residentEntries)
            {
                Enumerate(entry, result);
            }

            return result;
        }

        private void Enumerate(DirectoryEntry focus, List<DirectoryEntry> accumulator)
        {
            if ((focus.Flags & IndexEntryFlags.Node) != 0)
            {
                _indexStream.Position = focus.ChildrenVirtualCluster * _fileSystem.BytesPerCluster;
                byte[] buffer = Utilities.ReadFully(_indexStream, _fileSystem.BiosParameterBlock.IndexBufferSize);
                IndexBlock<FileNameRecord, FileReference> block = new IndexBlock<FileNameRecord, FileReference>(_fileSystem.BiosParameterBlock.BytesPerSector);
                block.FromBytes(buffer, 0);
                buffer = null;

                foreach (DirectoryEntry entry in block.IndexEntries)
                {
                    Enumerate(entry, accumulator);
                }
            }

            if ((focus.Flags & IndexEntryFlags.End) == 0)
            {
                accumulator.Add(focus);
                //accumulator.Add(_fileSystem.MasterFileTable.GetFileOrDirectory(focus.Data));
            }
        }

        public override void Dump(TextWriter writer, string indent)
        {
            writer.WriteLine(indent + "DIRECTORY (" + _baseRecord.ToString() + ")");
            writer.WriteLine(indent + "  File Number: " + _baseRecord.MasterFileTableIndex);

            foreach (FileAttributeRecord attrRec in _baseRecord.Attributes)
            {
                FileAttribute.FromRecord(_fileSystem, attrRec).Dump(writer, indent + "  ");
            }
        }

        public override string ToString()
        {
            return base.ToString() + @"\";
        }
    }
}