#!/usr/bin/env python3
import sys
import os
import struct

MAGIC = 0x445254494E49534F  # 'OSINITRD'

def align_up(val, align):
    return (val + align - 1) & ~(align - 1)

def main():
    if len(sys.argv) < 3:
        print("Usage: Pack-Initrd.py <output.img> <name1=file1> [name2=file2 ...]")
        sys.exit(1)

    out_path = sys.argv[1]
    entries = []

    for arg in sys.argv[2:]:
        if '=' not in arg:
            print(f"Error: Invalid argument '{arg}'. Expected format name=filepath")
            sys.exit(1)
        name, filepath = arg.split('=', 1)
        if not os.path.isfile(filepath):
            print(f"Error: File '{filepath}' not found")
            sys.exit(1)
        with open(filepath, 'rb') as f:
            data = f.read()
        entries.append((name, data))

    entry_count = len(entries)
    # Header: 16 bytes
    # Entry table: entry_count * 48 bytes (32 name + 8 offset + 8 length)
    table_size = 16 + (entry_count * 48)
    first_payload_offset = align_up(table_size, 4096)

    # Compute offsets for each payload
    payload_offsets = []
    current_offset = first_payload_offset
    for name, data in entries:
        payload_offsets.append(current_offset)
        current_offset = align_up(current_offset + len(data), 4096)

    total_archive_size = current_offset

    out_dir = os.path.dirname(out_path)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)

    with open(out_path, 'wb') as out_f:
        # 16-byte header
        out_f.write(struct.pack('<QII', MAGIC, entry_count, total_archive_size))

        # Entry headers
        for (name, data), offset in zip(entries, payload_offsets):
            name_bytes = name.encode('utf-8')[:31].ljust(32, b'\x00')
            out_f.write(struct.pack('<32sQQ', name_bytes, offset, len(data)))

        # Write payloads at aligned offsets
        for (name, data), offset in zip(entries, payload_offsets):
            pad_needed = offset - out_f.tell()
            if pad_needed > 0:
                out_f.write(b'\x00' * pad_needed)
            out_f.write(data)

        # Pad to total size
        pad_end = total_archive_size - out_f.tell()
        if pad_end > 0:
            out_f.write(b'\x00' * pad_end)

    print(f"[PACK-INITRD] Successfully created '{out_path}' ({total_archive_size} bytes, {entry_count} entries)")

if __name__ == '__main__':
    main()
