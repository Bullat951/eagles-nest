import re, sys, zipfile, collections
vti = open(sys.argv[1], "rb").read()
names = [m.group().decode() for m in re.finditer(rb"[A-Za-z0-9_\-\.]{3,80}\.(tif|dds|png|bmp)", vti)]
c = collections.Counter(names)
print(f"vti references {len(names)} names, {len(c)} distinct; sample: {list(c)[:25]}")
for zp in sys.argv[2:]:
    z = zipfile.ZipFile(zp)
    infos = z.infolist()
    exts = collections.Counter(i.filename.rsplit('.', 1)[-1].lower() for i in infos)
    big = sorted(infos, key=lambda i: -i.file_size)[:8]
    print(f"== {zp.split(chr(92))[-1]}: {len(infos)} entries, {exts.most_common(5)}")
    print("  sample:", [i.filename for i in infos[:15]])
    print("  biggest:", [(i.filename, i.file_size // 1024) for i in big])
    inv = set(i.filename.lower() for i in infos)
    hit = sum(1 for n in c if n.lower() in inv or (n.lower() + '.dds') in inv)
    print(f"  vti names found in this zip: {hit} of {len(c)}")
