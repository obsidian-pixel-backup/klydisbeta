import re

tensors = set()
with open("llama_native.log", "r") as f:
    for line in f:
        m = re.search(r"create_tensor: loading tensor (.+)", line)
        if m:
            tensors.add(m.group(1).strip())

print(f"Total unique tensors created by llama.cpp: {len(tensors)}")

# Let's count by prefixes
prefixes = {}
for t in tensors:
    prefix = t.split('.')[0]
    if prefix.startswith('blk'):
        prefix = 'blk'
    prefixes[prefix] = prefixes.get(prefix, 0) + 1

print("Tensors by prefix:")
for p, c in prefixes.items():
    print(f"{p}: {c}")

# Let's find out what the 15 extra tensors might be
print("Non-blk tensors:")
for t in tensors:
    if not t.startswith('blk'):
        print(t)
