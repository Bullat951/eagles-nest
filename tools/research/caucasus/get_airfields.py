# Reads the airfields message from a local DCB server and writes runway centres/headings/lengths as JSON.
# usage: get_airfields.py <password> <out.json>   (password via argv; local test server only)
import json, socket, sys

pw, out = sys.argv[1], sys.argv[2]
s = socket.create_connection(("127.0.0.1", 10309), timeout=60)
s.sendall((json.dumps({"type": "auth", "password": pw, "client": "probe"}) + "\n").encode())
buf = b""
while True:
    chunk = s.recv(1 << 20)
    if not chunk:
        break
    buf += chunk
    while b"\n" in buf:
        line, buf = buf.split(b"\n", 1)
        try:
            m = json.loads(line)
        except ValueError:
            continue
        if m.get("type") == "airfields":
            fields = [{"name": a["name"], "runways": [r for r in a.get("runways", [])]}
                      for a in m["airfields"]]
            json.dump(fields, open(out, "w"), indent=1)
            print(f"{len(fields)} airfields; sample: {fields[0]}")
            sys.exit(0)

