"""Generate original vector branding and native Windows renditions; no downloaded assets or fonts."""
import json
import subprocess
import sys
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets/branding"
WPF = ROOT / "LenovoLegionToolkit.WPF"
SOURCE = json.loads((ASSETS / "geometry.json").read_text(encoding="utf-8"))
PALETTE = SOURCE["palette"]
JOBS = []


def emblem(micro=False, mono=None):
    return [{"path": p["path"], "fill": mono or PALETTE[p["fill"]]} for p in SOURCE["micro" if micro else "emblem"]]


def letters(text, x, y, scale, spacing, fill):
    shapes = []
    for letter in text:
        width, path = SOURCE["glyphs"][letter]
        shapes.append({"path": path, "fill": fill, "transform": [scale, scale, x, y]})
        x += (width + spacing) * scale
    return shapes


def wordmark(text_only=False, mono=None):
    if text_only:
        return (104, 32, letters("PROJECT", 0, 0, .55, 4, mono or PALETTE["steel"]) +
                letters("LEGION", 0, 12, 1.35, 2.7, mono or PALETTE["white"]))
    mark = emblem(mono=mono)
    for shape in mark:
        shape["transform"] = [1.65, 1.65, 0, 7]
    return (450, 120, mark + letters("PROJECT", 131, 8, 1.25, 4, mono or PALETTE["steel"]) +
            letters("LEGION", 131, 40, 5, 2, mono or PALETTE["white"]))


def svg(name, width, height, shapes, title):
    lines = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" role="img" aria-labelledby="title" color="#ECEFF4">',
             f'  <title id="title">{escape(title)}</title>']
    for shape in shapes:
        transform = shape.get("transform")
        attr = f' transform="matrix({transform[0]} 0 0 {transform[1]} {transform[2]} {transform[3]})"' if transform else ""
        lines.append(f'  <path fill="{shape["fill"]}" fill-rule="evenodd" d="{shape["path"]}"{attr}/>')
    lines.append("</svg>")
    (ASSETS / name).write_text("\n".join(lines) + "\n", encoding="utf-8")


def raster(path, width, height, shapes, canvas=(64, 64)):
    JOBS.append({"path": str(path), "width": width, "height": height,
                 "canvasWidth": canvas[0], "canvasHeight": canvas[1], "shapes": shapes})


def png_resources():
    images = {
        "ProjectLegionEmblem": "project-legion.png",
        "ProjectLegionSidebarEmblem": "sidebar-64.png",
        "ProjectLegionTitlebarEmblem": "titlebar-32.png",
        "ProjectLegionMonochromeEmblem": "monochrome.png",
        "ProjectLegionSidebarWordmark": "sidebar-wordmark.png",
        "ProjectLegionWordmark": "wordmark.png",
    }
    lines = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
             ' xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options">',
             '<!-- PNG renditions of the original geometry.json artwork; embedded, cached and frozen. -->']
    for key, filename in images.items():
        lines.append(f'    <BitmapImage x:Key="{key}" po:Freeze="True" CacheOption="OnLoad" UriSource="pack://application:,,,/Project%20Legion;component/Assets/Branding/{filename}"/>')
    return "\n".join(lines + ["</ResourceDictionary>"]) + "\n"


def main():
    brand_wpf = WPF / "Assets/Branding"
    brand_wpf.mkdir(parents=True, exist_ok=True)
    svg("project-legion.svg", 64, 64, emblem(), "Project Legion split-blade emblem")
    svg("sidebar.svg", 64, 64, emblem(micro=True), "Project Legion small sidebar emblem")
    svg("titlebar.svg", 64, 64, emblem(micro=True), "Project Legion title-bar emblem")
    svg("monochrome.svg", 64, 64, emblem(micro=True, mono="currentColor"), "Project Legion monochrome emblem")
    svg("monochrome-black.svg", 64, 64, emblem(micro=True, mono="#000000"), "Project Legion black emblem")
    w, h, words = wordmark()
    svg("wordmark.svg", w, h, words, "Project Legion")
    mw, mh, mono = wordmark(mono="currentColor")
    svg("wordmark-monochrome.svg", mw, mh, mono, "Project Legion monochrome wordmark")
    sw, sh, small_words = wordmark(text_only=True)
    svg("sidebar-wordmark.svg", sw, sh, small_words, "Project Legion sidebar wordmark")
    raster(ASSETS / "wordmark.png", 900, 240, words, (w, h))
    raster(ASSETS / "sidebar-wordmark.png", 208, 64, small_words, (sw, sh))
    raster(ASSETS / "monochrome.png", 256, 256, emblem(micro=True, mono=PALETTE["white"]))
    raster(ASSETS / "project-legion.png", 512, 512, emblem())
    raster(WPF / "Assets/ProjectLegion.png", 256, 256, emblem())
    raster(ROOT / "assets/logo.png", 512, 512, emblem())
    # Installer artwork uses its own black backing rather than alpha that Inno may flatten to white.
    installer_mark = [{"path": "M0 0 H64 V64 H0 Z", "fill": PALETTE["black"]}] + emblem()
    raster(ROOT / "InnoDependencies/Images/logo.png", 116, 116, installer_mark)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    tile = {"path": "M10 0 H54 Q64 0 64 10 V54 Q64 64 54 64 H10 Q0 64 0 54 V10 Q0 0 10 0 Z", "fill": "#000000"}
    for size in sizes:
        raster(ASSETS / f"icons/icon-{size}.png", size, size, [tile] + emblem(micro=size <= 32))
    for name, size in [("titlebar-16.png", 16), ("titlebar-32.png", 32), ("sidebar-32.png", 32), ("sidebar-64.png", 64)]:
        raster(ASSETS / name, size, size, emblem(micro=True))
    for path in sorted((ROOT / "LenovoLegionToolkit.LampArray/Images").glob("*.png")):
        name = path.name
        if "targetsize-" in name:
            size = int(name.split("targetsize-")[1].split("_")[0].split(".")[0])
            raster(path, size, size, emblem(micro=size <= 32, mono="#000000" if "lightunplated" in name else None))
        elif name.startswith("Wide310x150") or name.startswith("SplashScreen"):
            width, height = (620, 300) if name.startswith("Wide") else (1240, 600)
            # Center the same lockup, without stretching its aspect ratio.
            expanded = []
            for shape in words:
                t = shape.get("transform", [1, 1, 0, 0])
                expanded.append(dict(shape, transform=[t[0], t[1], t[2], t[3] + 48]))
            raster(path, width, height, expanded, (450, 216))
        else:
            size = 300 if name.startswith("Square150") else 88 if name.startswith("Square44") else 50 if name == "StoreLogo.png" else 96
            raster(path, size, size, emblem(micro=size <= 32))
    (brand_wpf / "Identity.xaml").write_text(png_resources(), encoding="utf-8")
    evidence = ROOT / "BuildProjectLegion/Branding"
    evidence.mkdir(parents=True, exist_ok=True)
    manifest = evidence / "render-jobs.json"
    manifest.write_text(json.dumps({"root": str(ROOT), "jobs": JOBS, "iconSizes": sizes}), encoding="utf-8")
    # Use an STA command host; do not change the user's PowerShell execution policy.
    renderer = (ROOT / "tools/render_brand_assets.ps1").read_text(encoding="utf-8")
    renderer = renderer.split("\n", 1)[1]
    command = "$Manifest = '" + str(manifest).replace("'", "''") + "'\n" + renderer
    subprocess.run(["powershell.exe", "-NoProfile", "-Sta", "-Command", command], cwd=ROOT, check=True)
    print(f"Generated original SVGs, transparent PNGs, embedded PNG resources and {len(sizes)}-size Windows ICO.")


if __name__ == "__main__":
    main()
