import concurrent.futures
import html.parser
import json
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path

def get(url, method="GET"):
    return urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent":"ROM-release-metadata"}, method=method), timeout=18)

def asset(label, filename, url, sha, size, group="base"):
    assert re.fullmatch("[a-fA-F0-9]{64}", sha), (filename, sha)
    return dict(label=label, filename=filename, url=url, sha256=sha.lower(), bytes=size, group=group)

def github(repo, tag, match, label):
    with get(f"https://api.github.com/repos/{repo}/releases/tags/{tag}") as result:
        assets=json.load(result)["assets"]
    record=next(x for x in assets if x["name"]==match)
    sha = (record.get("digest") or "").removeprefix("sha256:")
    if not sha:
        with get(record["browser_download_url"]+".sha256") as result:
            sha=result.read().decode().split()[0]
    return asset(label,match,record["browser_download_url"],sha,record["size"])

def pypi(package, version, label):
    with get(f"https://pypi.org/pypi/{package}/{version}/json") as result:
        urls=json.load(result)["urls"]
    def rank(rec):
        name=rec["filename"]
        if name.endswith("-cp312-cp312-win_amd64.whl"): return 5
        if re.search(r"-cp(?:3[789]|310|311|312)-abi3-win_amd64\.whl$",name): return 4
        if name.endswith(("-py3-none-win_amd64.whl","-py2.py3-none-win_amd64.whl")): return 3
        if name.endswith(("-py3-none-any.whl","-py2.py3-none-any.whl")): return 2
        return 0
    selected=max(urls,key=rank)
    assert rank(selected)>0, (package,version)
    return asset(label,selected["filename"],selected["url"],selected["digests"]["sha256"],selected["size"],"wheel")

class Links(html.parser.HTMLParser):
    def __init__(self):
        super().__init__()
        self.links=[]
    def handle_starttag(self, tag, attrs):
        if tag=="a":
            url=dict(attrs).get("href")
            if url: self.links.append(url)

def torch(flavour):
    index=f"https://download.pytorch.org/whl/{flavour}/torch/"
    with get(index) as result:
        parser=Links(); parser.feed(result.read().decode())
    name=f"torch-2.11.0+{flavour}-cp312-cp312-win_amd64.whl"
    for href in parser.links:
        url, frag=urllib.parse.urldefrag(urllib.parse.urljoin(index,href))
        if urllib.parse.unquote(url.split("/")[-1])==name:
            sha=frag.removeprefix("sha256=")
            with get(url,"HEAD") as result:
                size=int(result.headers.get("Content-Length",0))
            return asset("PyTorch ("+("NVIDIA GPU" if flavour=="cu128" else "CPU")+")",name,url,sha,size,flavour)
    raise ValueError(name)

def fixed(label, filename, url, sha):
    size=0
    try:
        with get(url,"HEAD") as result: size=int(result.headers.get("Content-Length",0))
    except Exception: pass
    return asset(label,filename,url,sha,size)

items=[
    ("numpy","2.5.5","NumPy"),
    ("soundfile","0.14.0","SoundFile"),
    ("cffi","2.1.1","CFFI"),
    ("pycparser","3.12","pycparser"),
    ("typing-extensions","4.16.0","Typing Extensions"),
    ("filelock","4.2.4","FileLock"),
    ("setuptools","78.1.0","Setuptools"),
    ("sympy","1.14.0","SymPy"),
    ("mpmath","1.3.0","mpmath"),
    ("networkx","3.7","NetworkX"),
    ("jinja2","3.1.6","Jinja2"),
    ("markupsafe","3.0.3","MarkupSafe"),
    ("fsspec","2026.9.0","fsspec"),
]
with concurrent.futures.ThreadPoolExecutor(max_workers=8) as workers:
    jobs=[
        workers.submit(github,"astral-sh/uv","0.11.14","uv-x86_64-pc-windows-msvc.zip","uv setup tool"),
        workers.submit(github,"astral-sh/python-build-standalone","20260510","cpython-3.12.13+20260510-x86_64-pc-windows-msvc-install_only_stripped.tar.gz","Python 3.12.13"),
        workers.submit(fixed,"FFmpeg video tools","ffmpeg-9.0.2-essentials_build.zip","https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip","60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba"),
        workers.submit(fixed,"Bandit cutscene model","checkpoint-multi.ckpt","https://zenodo.org/api/records/12701995/files/checkpoint-multi.ckpt/content","abcfccf65446752a057f4a302c941479a54b7560ebf8d7bca039d2ea98e64cfc"),
        *[workers.submit(pypi,*item) for item in items],
        workers.submit(torch,"cpu"), workers.submit(torch,"cu128"),
    ]
    results=[job.result() for job in jobs]
Path(sys.argv[1]).write_text(json.dumps(results,indent=2)+"\n",encoding="utf-8")
for rec in results:
    print(rec["filename"], rec["bytes"], urllib.parse.urlsplit(rec["url"]).netloc)
