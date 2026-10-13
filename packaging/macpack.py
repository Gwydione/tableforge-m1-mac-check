"""TableForge Mac Check packaging helper (development machine; runs on Windows, macOS or Linux; Python 3 standard library
plus Pillow for the icon). Three commands:

  bundle  --publish DIR --rid osx-arm64|osx-x64 --version V --build-id ID --icon PNG --out DIR
          Assembles "TableForge Mac Check.app" from a self-contained `dotnet publish` folder: Contents/MacOS (the publish
          output minus the corpus), Contents/Resources/corpus (the 8 PDFs + expected.json), Contents/Info.plist, AppIcon.icns.

  verify  --app APP --rid RID
          Checks every Mach-O file's architecture and whether it carries a code signature (and of which kind), the main
          executable, Info.plist, corpus, and that no development or personal data is inside. Prints JSON; exit 1 on failure.

  zip     --root DIR --out ZIP
          Writes a zip that keeps Unix permissions (executables 0755) — Windows' own zip tools lose them, which would leave
          the app unable to start on a Mac.

Nothing here signs the bundle itself: without Apple's codesign (macOS) or another signer, the .app has no bundle seal.
"""
import argparse, hashlib, json, os, plistlib, re, shutil, stat, struct, sys, zipfile

CPU = {0x01000007: "x86_64", 0x0100000C: "arm64"}
RID_ARCH = {"osx-arm64": "arm64", "osx-x64": "x86_64"}
EXECUTABLE = "TableForgeMacCheck"
BUNDLE_ID = "net.rpgfrequencies.tableforge.maccheck"


# ---- Mach-O inspection ----------------------------------------------------------------------------------------------

def macho_slices(data):
    """[(arch, offset)] for a thin or fat Mach-O; [] when the file is not Mach-O."""
    if len(data) < 8:
        return []
    magic_be = struct.unpack(">I", data[:4])[0]
    if magic_be in (0xCAFEBABE,):  # fat (universal)
        n = struct.unpack(">I", data[4:8])[0]
        if n > 10:
            return []  # Java class files share the magic; real fat files have a handful of slices
        out = []
        for i in range(n):
            cpu, _sub, off, _size, _align = struct.unpack(">IIIII", data[8 + i * 20: 28 + i * 20])
            out.append((CPU.get(cpu, hex(cpu)), off))
        return out
    if struct.unpack("<I", data[:4])[0] == 0xFEEDFACF:
        cpu = struct.unpack("<I", data[4:8])[0]
        return [(CPU.get(cpu, hex(cpu)), 0)]
    return []


def signature_kind(data, off):
    """'none', 'adhoc' or 'certificate' for the slice at off."""
    ncmds = struct.unpack("<I", data[off + 16: off + 20])[0]
    p = off + 32
    for _ in range(ncmds):
        cmd, size = struct.unpack("<II", data[p: p + 8])
        if cmd == 0x1D:  # LC_CODE_SIGNATURE
            sig_off, sig_size = struct.unpack("<II", data[p + 8: p + 16])
            blob = data[off + sig_off: off + sig_off + sig_size]
            if len(blob) < 12 or struct.unpack(">I", blob[:4])[0] != 0xFADE0CC0:
                return "invalid"
            count = struct.unpack(">I", blob[8:12])[0]
            for i in range(count):
                typ, boff = struct.unpack(">II", blob[12 + i * 8: 20 + i * 8])
                if typ == 0x10000:  # CMS blob: present and non-empty = certificate signature
                    length = struct.unpack(">I", blob[boff + 4: boff + 8])[0]
                    return "certificate" if length > 8 else "adhoc"
            return "adhoc"
        p += size
    return "none"


# ---- bundle -----------------------------------------------------------------------------------------------------------

def make_icns(png, out):
    from PIL import Image
    img = Image.open(png).convert("RGBA")
    img.save(out, format="ICNS", sizes=[(16, 16), (32, 32), (64, 64), (128, 128), (256, 256), (512, 512), (1024, 1024)])


def bundle(a):
    app = os.path.join(a.out, "TableForge Mac Check.app")
    if os.path.exists(app):
        shutil.rmtree(app)
    macos = os.path.join(app, "Contents", "MacOS")
    resources = os.path.join(app, "Contents", "Resources")
    shutil.copytree(a.publish, macos, ignore=shutil.ignore_patterns("corpus", "*.pdb"))
    shutil.copytree(os.path.join(a.publish, "corpus"), os.path.join(resources, "corpus"))
    make_icns(a.icon, os.path.join(resources, "AppIcon.icns"))
    info = {
        "CFBundleName": "TableForge Mac Check",
        "CFBundleDisplayName": "TableForge Mac Check",
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleExecutable": EXECUTABLE,
        "CFBundleIconFile": "AppIcon",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": a.version,
        "CFBundleVersion": a.build_id,
        "CFBundleInfoDictionaryVersion": "6.0",
        "LSMinimumSystemVersion": "14.0",  # .NET 10 supports macOS 14 and later
        "NSHighResolutionCapable": True,
        "LSApplicationCategoryType": "public.app-category.developer-tools",
        "NSHumanReadableCopyright": "Test tool for TableForge (RPG Frequencies). Not for distribution.",
        # The dice page is served from http://127.0.0.1 (a loopback page server inside the app) into WKWebView.
        "NSAppTransportSecurity": {"NSAllowsLocalNetworking": True},
    }
    with open(os.path.join(app, "Contents", "Info.plist"), "wb") as f:
        plistlib.dump(info, f)
    with open(os.path.join(app, "Contents", "PkgInfo"), "w") as f:
        f.write("APPL????")
    print(app)


# ---- verify -----------------------------------------------------------------------------------------------------------

FORBIDDEN_TEXT = [r"[A-Za-z]:\\Users\\", r"/Users/[a-z]", "tableforge.db", "dddice-account.json", "TABLEFORGE_DATA_DIR=", "e2-fixtures"]


def verify(a):
    want = RID_ARCH[a.rid]
    report = {"rid": a.rid, "expectedArch": want, "machO": [], "problems": [], "warnings": []}
    contents = os.path.join(a.app, "Contents")
    try:
        with open(os.path.join(contents, "Info.plist"), "rb") as f:
            info = plistlib.load(f)
        report["info"] = {k: info.get(k) for k in ("CFBundleIdentifier", "CFBundleExecutable", "CFBundleShortVersionString", "CFBundleVersion", "LSMinimumSystemVersion")}
        if info.get("CFBundleExecutable") != EXECUTABLE:
            report["problems"].append("Info.plist CFBundleExecutable mismatch")
    except Exception as e:
        report["problems"].append(f"Info.plist unreadable: {e}")
    main = os.path.join(contents, "MacOS", EXECUTABLE)
    if not os.path.isfile(main):
        report["problems"].append("main executable missing")
    pdfs = [f for f in os.listdir(os.path.join(contents, "Resources", "corpus")) if not f.startswith(".")] if os.path.isdir(os.path.join(contents, "Resources", "corpus")) else []
    report["corpusCases"] = sorted(pdfs)
    for case in pdfs:
        d = os.path.join(contents, "Resources", "corpus", case)
        if os.path.isdir(d) and not (os.path.isfile(os.path.join(d, case + ".pdf")) and os.path.isfile(os.path.join(d, "expected.json"))):
            report["problems"].append(f"corpus case incomplete: {case}")
    if len([p for p in pdfs if p.startswith("c0")]) != 8:
        report["problems"].append(f"expected 8 corpus cases, found {len(pdfs)}")
    for root, _dirs, files in os.walk(a.app):
        for name in files:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, a.app).replace("\\", "/")
            with open(path, "rb") as f:
                data = f.read()
            slices = macho_slices(data)
            if slices:
                archs = [s[0] for s in slices]
                sigs = {arch: signature_kind(data, off) for arch, off in slices}
                report["machO"].append({"file": rel, "archs": archs, "signature": sigs.get(want, sigs)})
                if want not in archs:
                    report["problems"].append(f"{rel}: no {want} code ({archs})")
                elif sigs.get(want) in ("none", "invalid"):
                    report["problems"].append(f"{rel}: {want} code has no valid signature ({sigs.get(want)}) — Apple Silicon refuses unsigned code" if want == "arm64" else f"{rel}: unsigned")
            if name.endswith((".json", ".txt", ".plist", ".html", ".config", ".xml")) and len(data) < 5_000_000:
                text = data.decode("utf-8", "replace")
                for pat in FORBIDDEN_TEXT:
                    if re.search(pat, text):
                        report["problems"].append(f"{rel}: contains forbidden text /{pat}/")
    main_sig = next((m["signature"] for m in report["machO"] if m["file"] == f"Contents/MacOS/{EXECUTABLE}"), None)
    report["mainExecutableSignature"] = main_sig
    report["bundleSeal"] = os.path.isfile(os.path.join(contents, "_CodeSignature", "CodeResources"))
    if not report["bundleSeal"]:
        report["warnings"].append("no bundle seal (Contents/_CodeSignature): Gatekeeper behaviour for this layout is unverified")
    report["ok"] = not report["problems"]
    print(json.dumps(report, indent=2))
    return 0 if report["ok"] else 1


# ---- zip --------------------------------------------------------------------------------------------------------------

def is_executable(path):
    if os.path.basename(path) == EXECUTABLE or os.path.basename(path) == "createdump":
        return True
    with open(path, "rb") as f:
        head = f.read(4096)  # enough for a fat header's slice table
    return bool(macho_slices(head)) and not path.endswith(".dylib")


def make_zip(a):
    root = os.path.abspath(a.root)
    base = os.path.dirname(root)
    with zipfile.ZipFile(a.out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for d, dirs, files in os.walk(root):
            dirs.sort()
            rel_dir = os.path.relpath(d, base).replace("\\", "/")
            info = zipfile.ZipInfo(rel_dir + "/")
            info.create_system = 3
            info.external_attr = (stat.S_IFDIR | 0o755) << 16 | 0x10
            z.writestr(info, b"")
            for name in sorted(files):
                path = os.path.join(d, name)
                info = zipfile.ZipInfo(os.path.relpath(path, base).replace("\\", "/"))
                info.create_system = 3  # Unix: macOS Archive Utility honours the mode bits below
                mode = 0o755 if is_executable(path) else 0o644
                info.external_attr = (stat.S_IFREG | mode) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(path, "rb") as f:
                    z.writestr(info, f.read())
    h = hashlib.sha256(open(a.out, "rb").read()).hexdigest()
    print(json.dumps({"zip": a.out, "sha256": h, "bytes": os.path.getsize(a.out)}))


def zipcheck(a):
    """Lists the mode bits a Mac would apply, for the main executable and a sample of files."""
    with zipfile.ZipFile(a.zip) as z:
        rows = [(i.filename, oct((i.external_attr >> 16) & 0o777), i.create_system) for i in z.infolist()]
    exe = [r for r in rows if r[0].endswith("/Contents/MacOS/" + EXECUTABLE)]
    print(json.dumps({"entries": len(rows), "mainExecutable": exe, "executables": [r for r in rows if r[1] == "0o755" and not r[0].endswith("/")]}, indent=1))
    return 0 if exe and exe[0][1] == "0o755" and exe[0][2] == 3 else 1


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("bundle"); b.add_argument("--publish", required=True); b.add_argument("--rid", required=True); b.add_argument("--version", required=True); b.add_argument("--build-id", required=True); b.add_argument("--icon", required=True); b.add_argument("--out", required=True)
    v = sub.add_parser("verify"); v.add_argument("--app", required=True); v.add_argument("--rid", required=True)
    zz = sub.add_parser("zip"); zz.add_argument("--root", required=True); zz.add_argument("--out", required=True)
    zc = sub.add_parser("zipcheck"); zc.add_argument("--zip", required=True)
    a = p.parse_args()
    sys.exit({"bundle": bundle, "verify": verify, "zip": make_zip, "zipcheck": zipcheck}[a.cmd](a) or 0)
