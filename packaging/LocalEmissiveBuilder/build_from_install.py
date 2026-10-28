"""Build a local cosmetic DB from the player's installed xrMPE archives.

No game archive or executable is written. The output is intentionally local-only.
"""
from pathlib import Path
import hashlib
import json
import os
import runpy
import shutil
import sys
import tempfile
import uuid

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from read_db import Archive, checked_path, tar_members

EXPECTED_ARCHIVE = '1BBADB9EDB4FAB5C82815AF7A74C87A813AF356E6F65D1A8486CDED90E608081'
EXPECTED_ADDON = '9B6E69515CD2ED8E60B19F2B2933E7F46E7A322ECD8B4DCDAB1A9B0B63D65FE6'
# The 2026-10-25 PvP 1.4.7 revision has the same 2964 non-config DB0
# resources. Only deathmatch_game.ltx and mp_ranks.ltx differ; neither is
# consumed by this cosmetic builder. Both full-file hashes remain pinned.
EXPECTED_ADDON_UPDATED = '670D1A3932DA428832345039E5506EBA5E105968D8CC5A7DC28AEC14BB00FD4C'

class CompatibilityError(Exception):
    def __init__(self, code, file, expected='', actual='', size=0, version='', accepted=None):
        super().__init__(code)
        self.details = dict(code=code, file=file, expected_sha256=expected,
                            actual_sha256=actual, size_bytes=size, addon_version=version,
                            supported_sha256=accepted or [expected])


def addon_version(path):
    try:
        member = next(((start, size) for name, start, size in tar_members(path)
                       if name == 'metadata.json' and size <= 65536), None)
        if member is None:
            return ''
        with path.open('rb') as source:
            source.seek(member[0])
            value = json.loads(source.read(member[1])).get('addonVersion', '')
        return value[:64] if isinstance(value, str) else ''
    except (OSError, ValueError, KeyError, TypeError, UnicodeError):
        return ''


def verify_installed_file(path, expected, relative, version_reader=None, accepted=None):
    trusted = [expected] + list(accepted or [])
    if not path.is_file():
        raise CompatibilityError('missing', relative, expected, accepted=trusted)
    try:
        size = path.stat().st_size
        with path.open('rb') as source:
            actual = hashlib.file_digest(source, 'sha256').hexdigest().upper()
    except OSError:
        raise CompatibilityError('unreadable', relative, expected, accepted=trusted) from None
    if actual not in trusted:
        version = version_reader(path) if version_reader else ''
        raise CompatibilityError('hash_mismatch', relative, expected, actual, size, version, trusted)
    return actual

def main():
    if len(sys.argv) != 3:
        raise SystemExit('Usage: build_from_install.py GAME_DIRECTORY OUTPUT_DB')
    game = Path(sys.argv[1]).resolve(strict=True)
    output = Path(sys.argv[2]).resolve()
    print('ZL_PROGRESS|5|Проверяем установленные архивы', flush=True)
    if output.is_relative_to(game) or game.is_relative_to(output.parent):
        raise ValueError('Refusing to place generated content in the game directory')
    archive_path = game / 'patches' / 'xrmpe_meshes_actors.db0'
    verify_installed_file(archive_path, EXPECTED_ARCHIVE, 'patches/xrmpe_meshes_actors.db0')
    addon_path = game / 'addons' / 'xrmpe_pvp.addon'
    verify_installed_file(addon_path, EXPECTED_ADDON, 'addons/xrmpe_pvp.addon',
                          addon_version, [EXPECTED_ADDON_UPDATED])
    costumes = json.loads((HERE / 'costumes.json').read_text(encoding='utf-8-sig'))
    names = {Path(row['model'].replace('\\', '/')).name for row in costumes}
    names.update(('stalker_neutral_1_face_1.ogf', 'stalker_neutral_1_mask.ogf',
                  'stalker_neutral_1.ogf', 'stalker_tdm_team_green_1.ogf'))
    model_names = {'meshes\\actors\\stalker_mp\\' + name for name in names}
    model_names.add('textures\\act\\act_mp_tdm_green_1.dds')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.zonelink-emissive-', dir=output.parent) as temporary:
        work = Path(temporary)
        extracted = work / 'extracted' / ('patches_' + archive_path.name)
        archive = Archive(archive_path)
        addon_member = next(((start, size) for name, start, size in tar_members(addon_path)
                             if name == 'xrmpe_pvp_addon.db0'), None)
        if addon_member is None:
            raise ValueError('PvP actor database is missing from the addon')
        addon_archive = Archive(addon_path, *addon_member)
        available = {row['name'].lower(): (archive, row['name']) for row in archive.entries}
        available.update({row['name'].lower(): (addon_archive, row['name']) for row in addon_archive.entries
                          if row['name'].lower() not in available})
        for name in sorted(model_names):
            found = available.get(name.lower())
            if found is None:
                raise ValueError('Required installed resource is missing: ' + name)
            container, actual = found
            target = extracted / checked_path(name)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(container.read(actual))
        import inspect_geometry as geometry
        geometry.INPUT = work / 'extracted'
        geometry.SOURCE = extracted / 'meshes' / 'actors' / 'stalker_mp'
        os.environ['ZL_BUILD_HERE'] = str(work)
        print('ZL_PROGRESS|15|Собираем модели повязок', flush=True)
        runpy.run_path(str(HERE / 'build_local.py'), run_name='__main__')
        result = work / 'LocalMods' / 'zonelink_emissive_30.db'
        validation = json.loads((work / 'VALIDATION.json').read_text(encoding='utf-8'))
        if validation['purchased_candidates'] != 30 or validation['starter_candidates'] != 3 or validation['issues']:
            raise ValueError('Local model generation did not pass structural checks')
        with result.open('rb') as source:
            actual_digest = hashlib.file_digest(source, 'sha256').hexdigest().upper()
        if actual_digest != validation['db_sha256']:
            raise ValueError('Generated archive checksum mismatch')
        # Previews are rendered only on this machine from the installed model
        # and texture bytes. No game-derived image ships with the launcher.
        import inspect_geometry as geometry
        geometry.SOURCE = work / 'LocalMods' / 'meshes' / 'actors' / 'stalker_mp'
        print('ZL_PROGRESS|48|Находим текстуры костюмов', flush=True)
        texture_names = set()
        for row in costumes:
            for part in geometry.load(Path(row['model'].replace('\\', '/')).stem)[0]:
                texture_names.add('textures\\' + part['texture'] + '.dds')
        missing = {name.lower() for name in texture_names if name.lower() not in available}
        if missing:
            for path in sorted((game / 'resources').glob('vanilla.db*')):
                resource = Archive(path)
                for row in resource.entries:
                    key = row['name'].lower()
                    if key in missing:
                        available[key] = (resource, row['name'])
                        missing.remove(key)
                if not missing:
                    break
        for name in sorted(texture_names):
            target = extracted / checked_path(name)
            if target.exists():
                continue
            found = available.get(name.lower())
            if found is None:
                raise ValueError('Preview texture is missing from installed xrMPE: ' + name)
            container, actual = found
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(container.read(actual))
        # Render away from the public cache. A complete directory becomes visible
        # only after all 120 PNG files have been written successfully.
        previews = output.parent / 'Previews'
        staged_previews = output.parent / ('.Previews-' + uuid.uuid4().hex)
        os.environ['ZL_PREVIEW_OUTPUT'] = str(staged_previews)
        print('ZL_PROGRESS|55|Создаём 120 предпросмотров', flush=True)
        try:
            runpy.run_path(str(HERE / 'render_previews.py'), run_name='__main__')
            print('ZL_PROGRESS|96|Проверяем готовый пакет', flush=True)
            staged = output.with_name(output.name + '.tmp')
            if staged.exists():
                staged.unlink()
            shutil.copyfile(result, staged)
            previous_previews = output.parent / ('.Previews-old-' + uuid.uuid4().hex)
            if previews.exists():
                os.replace(previews, previous_previews)
            try:
                os.replace(staged_previews, previews)
                os.replace(staged, output)
            except Exception:
                if previous_previews.exists():
                    if previews.exists():
                        shutil.rmtree(previews)
                    os.replace(previous_previews, previews)
                raise
            if previous_previews.exists():
                shutil.rmtree(previous_previews)
        finally:
            if staged_previews.exists():
                shutil.rmtree(staged_previews)
        print('ZL_PROGRESS|100|3D-повязки готовы', flush=True)
        print(json.dumps({'db_sha256': actual_digest, 'entries': validation['archive_entries'],
                          'models': 33, 'output': str(output)}, ensure_ascii=False))

if __name__ == '__main__':
    try:
        main()
    except CompatibilityError as error:
        print('ZL_DIAGNOSTIC|' + json.dumps(error.details, ensure_ascii=False), file=sys.stderr)
        raise SystemExit(2) from None
