import json
import sys
import os

path = sys.argv[1] if len(sys.argv) > 1 else None
if not path:
    # Try to find input.json in common locations
    for root, dirs, files in os.walk(r"D:\SourceCode\C#\C99\obj"):
        for f in files:
            if f == "input.json":
                path = os.path.join(root, f)
                break

if not path or not os.path.exists(path):
    print(f"input.json not found: {path}", file=sys.stderr)
    sys.exit(0)

try:
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)

    changed = False

    # LocalAssembly must be a list
    if data.get("LocalAssembly") is None:
        data["LocalAssembly"] = []
        changed = True

    # ClIncludeFiles must be a list
    if data.get("ClIncludeFiles") is None:
        data["ClIncludeFiles"] = []
        changed = True

    # CIncludeDirectories must be a string
    if data.get("CIncludeDirectories") is None:
        data["CIncludeDirectories"] = ""
        changed = True

    if changed:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False)
        print(f"Fixed {path}")
    else:
        print(f"No fix needed for {path}")

except Exception as e:
    print(f"Error: {e}", file=sys.stderr)
    sys.exit(0)
