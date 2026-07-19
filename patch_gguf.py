import sys
import struct
from gguf import GGUFReader

def patch_gguf(file_path):
    print(f"Reading {file_path}...")
    reader = GGUFReader(file_path)
    data_offset = reader.data_offset
    print(f"Data offset: {data_offset}")
    
    with open(file_path, "r+b") as f:
        f.seek(0)
        header_data = f.read(data_offset)
        
        original_len = len(header_data)
        modified_data = header_data.replace(b"blk.32.", b"mtp.32.")
        
        if modified_data == header_data:
            print("No blk.32. tensors found to patch.")
        else:
            if len(modified_data) != original_len:
                print("Error: Length changed!")
                return
            f.seek(0)
            f.write(modified_data)
            print("Patched successfully!")

if __name__ == "__main__":
    patch_gguf(r"C:\Users\corne\.klydis\models\Qwythos-9B-Claude-Mythos-5-1M-MTP-Q4_K_M.gguf")
