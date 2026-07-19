import os
import struct

def parse_and_rewrite_gguf(in_path, out_path, num_dummies=15):
    with open(in_path, "rb") as f_in, open(out_path, "wb") as f_out:
        # Read header
        magic = f_in.read(4)
        if magic != b"GGUF":
            raise ValueError("Not a GGUF file")
        version = struct.unpack("<I", f_in.read(4))[0]
        tensor_count = struct.unpack("<Q", f_in.read(8))[0]
        kv_count = struct.unpack("<Q", f_in.read(8))[0]
        
        # Write new header
        f_out.write(magic)
        f_out.write(struct.pack("<I", version))
        f_out.write(struct.pack("<Q", tensor_count + num_dummies))
        f_out.write(struct.pack("<Q", kv_count))
        
        # Copy KVs
        for _ in range(kv_count):
            # Key length + key
            klen = struct.unpack("<Q", f_in.read(8))[0]
            f_out.write(struct.pack("<Q", klen))
            f_out.write(f_in.read(klen))
            # Value type
            vtype = struct.unpack("<I", f_in.read(4))[0]
            f_out.write(struct.pack("<I", vtype))
            
            # Value
            def copy_value(vt):
                if vt == 8: # String
                    vlen = struct.unpack("<Q", f_in.read(8))[0]
                    f_out.write(struct.pack("<Q", vlen))
                    f_out.write(f_in.read(vlen))
                elif vt == 9: # Array
                    atype = struct.unpack("<I", f_in.read(4))[0]
                    f_out.write(struct.pack("<I", atype))
                    alen = struct.unpack("<Q", f_in.read(8))[0]
                    f_out.write(struct.pack("<Q", alen))
                    for _ in range(alen):
                        copy_value(atype)
                else:
                    # fixed sizes
                    sizes = {0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 8, 10: 8, 11: 8}
                    if vt in sizes:
                        f_out.write(f_in.read(sizes[vt]))
                    else:
                        raise ValueError(f"Unknown type {vt}")
            
            copy_value(vtype)
            
        # Copy tensor infos
        for _ in range(tensor_count):
            # Name
            nlen = struct.unpack("<Q", f_in.read(8))[0]
            f_out.write(struct.pack("<Q", nlen))
            f_out.write(f_in.read(nlen))
            # n_dims
            ndims = struct.unpack("<I", f_in.read(4))[0]
            f_out.write(struct.pack("<I", ndims))
            # dims
            f_out.write(f_in.read(ndims * 8))
            # type
            f_out.write(f_in.read(4))
            # offset
            f_out.write(f_in.read(8))
            
        # Append dummies
        for i in range(num_dummies):
            name = f"dummy_{i}".encode("utf-8")
            f_out.write(struct.pack("<Q", len(name)))
            f_out.write(name)
            f_out.write(struct.pack("<I", 1)) # n_dims = 1
            f_out.write(struct.pack("<Q", 1)) # dims[0] = 1
            f_out.write(struct.pack("<I", 0)) # type = f32
            f_out.write(struct.pack("<Q", 0)) # offset = 0
            
        # Alignment padding
        alignment = 32
        curr_pos = f_out.tell()
        padding = (alignment - (curr_pos % alignment)) % alignment
        f_out.write(b"\x00" * padding)
        
        # Copy remaining data (tensor bytes)
        # We need to skip the original padding
        curr_in = f_in.tell()
        padding_in = (alignment - (curr_in % alignment)) % alignment
        f_in.seek(padding_in, 1)
        
        # Stream copy
        while True:
            chunk = f_in.read(1024 * 1024 * 16) # 16 MB chunks
            if not chunk:
                break
            f_out.write(chunk)

if __name__ == "__main__":
    in_file = r"C:\Users\corne\.klydis\models\Qwythos-9B-Claude-Mythos-5-1M-MTP-Q4_K_M.gguf"
    out_file = in_file + ".new"
    print("Rewriting GGUF...")
    parse_and_rewrite_gguf(in_file, out_file, 15)
    print("Done. Replacing file...")
    os.replace(out_file, in_file)
    print("Success!")
