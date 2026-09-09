#!/usr/bin/env python3
"""Package only the plugin DLL and generate the public Jellyfin catalog."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from zipfile import ZipFile, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'src/Jellyfin.Plugin.ExtendedFilmography/Jellyfin.Plugin.ExtendedFilmography.csproj'
DLL_NAME = 'Jellyfin.Plugin.ExtendedFilmography.dll'
BASE_URL = 'https://zillywallfe.github.io/jellyfin-plugin-extendedfilmography'


def package(root=ROOT):
    project = ET.parse(root / PROJECT.relative_to(ROOT)).getroot()
    version = project.findtext('./PropertyGroup/Version')
    if not version or len(version.split('.')) != 4 or not all(v.isdigit() for v in version.split('.')):
        raise ValueError('Version must have four numeric parts')
    dll = root / 'artifacts/publish' / DLL_NAME
    if not dll.is_file() or not dll.stat().st_size:
        raise ValueError('Build the plugin before packaging')
    site = root / '_site'
    site.mkdir(exist_ok=True)
    filename = f'extended-filmography_{version}.zip'
    archive = site / filename
    with ZipFile(archive, 'w', compression=ZIP_DEFLATED) as bundle:
        bundle.write(dll, DLL_NAME)
    template = json.loads((root / 'manifest.json').read_text())
    entry = template[0]
    entry['owner'] = 'zillywallfe'
    entry['versions'] = [{
        'version': version,
        'changelog': 'Library duplicate filtering and cache fixes; broader discovery defaults.',
        'targetAbi': '10.11.11.0',
        'sourceUrl': f'{BASE_URL}/{filename}',
        'checksum': hashlib.md5(archive.read_bytes()).hexdigest(),
        'timestamp': '2026-09-09T00:00:00Z',
    }]
    (site / 'manifest.json').write_text(json.dumps(template, indent=2) + '\n')
    (site / '.nojekyll').touch()
    (site / 'index.html').write_text(
        '<!doctype html><html lang="en"><meta charset="utf-8">'
        '<title>Extended Filmography</title><h1>Extended Filmography</h1>'
        '<p>Jellyfin plugin repository by zillywallfe.</p>'
        '<p>Add <a href="manifest.json">this catalog URL</a> in Jellyfin’s plugin repositories.</p>'
        '<p><a href="https://github.com/zillywallfe/jellyfin-plugin-extendedfilmography">Source code</a></p></html>'
    )
    print(f'Packaged {filename}; catalog: {BASE_URL}/manifest.json')


if __name__ == '__main__':
    package()
