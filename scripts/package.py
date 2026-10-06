"""Publish a complete build at release/; optionally export a versioned ZIP."""
import argparse
from contextlib import ExitStack, contextmanager
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import uuid
import zipfile
import xml.etree.ElementTree as ET


WORKER_FILES = (
    'OutfitStudio.Worker.exe', 'OutfitStudio.Worker.dll',
    'OutfitStudio.Worker.deps.json', 'OutfitStudio.Worker.runtimeconfig.json',
    'OutfitStudio.Core.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'System.Private.CoreLib.dll',
)
PUBLIC_DOCUMENTS = ('README.md', 'CHANGELOG.md', 'docs/VERIFICATION.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')
NOTICE_COMPONENTS = ('dotnet-runtime', 'dotnet-host')
NOTICE_FILES = ('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT', 'version.txt')


def verify_runtime_notices(root, worker):
    """Refuse a runtime upgrade until its matching redistribution notices exist."""
    try:
        dependencies = json.loads((worker / 'OutfitStudio.Worker.deps.json').read_text(encoding='utf-8-sig'))
        runtime_versions = [name.split('/', 1)[1] for name in dependencies['libraries']
                            if name.startswith('runtimepack.Microsoft.NETCore.App.Runtime.win-x64/')]
        if len(runtime_versions) != 1:
            raise ValueError('expected exactly one Windows x64 .NET runtime pack')
        version = runtime_versions[0]
        configuration = json.loads((worker / 'OutfitStudio.Worker.runtimeconfig.json').read_text(encoding='utf-8-sig'))
        framework_versions = [framework['version'] for framework in configuration['runtimeOptions']['includedFrameworks']
                              if framework['name'] == 'Microsoft.NETCore.App']
        if framework_versions != [version]:
            raise ValueError('worker runtime configuration and dependency versions differ')
        for component in NOTICE_COMPONENTS:
            recorded_version = (root / 'licenses' / component / 'version.txt').read_text(encoding='utf-8-sig').strip()
            if recorded_version != version:
                raise ValueError(f'{component} notices are for {recorded_version}, but the published runtime is {version}')
    except (KeyError, TypeError, ValueError) as error:
        raise RuntimeError(f'Cannot package matching .NET runtime notices: {error}') from error


def public_files():
    return (*PUBLIC_DOCUMENTS, *(f'licenses/{component}/{name}'
                                for component in NOTICE_COMPONENTS for name in NOTICE_FILES))


def ignore_symbols(directory, names):
    # Publish can retain stale symbols even after DebugType changes. Never let a
    # stale PDB, including differently cased Windows filenames, reach the ZIP.
    return [name for name in names if Path(name).suffix.casefold() == '.pdb']


@contextmanager
def windows_update_handles(directory):
    """Reject files in use, and prevent new readers during the directory swap."""
    with ExitStack() as handles:
        if os.name == 'nt':
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD,
                                          wintypes.DWORD, wintypes.LPVOID,
                                          wintypes.DWORD, wintypes.DWORD,
                                          wintypes.HANDLE]
            kernel.CreateFileW.restype = wintypes.HANDLE
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            kernel.CloseHandle.restype = wintypes.BOOL
            for file in directory.rglob('*'):
                if file.is_file():
                    # Read/write access, share-delete only: our rename is allowed,
                    # while another process cannot start reading half an update.
                    handle = kernel.CreateFileW(str(file), 0xC0000000, 4,
                                                None, 3, 0x80, None)
                    if handle == wintypes.HANDLE(-1).value:
                        raise ctypes.WinError(ctypes.get_last_error())
                    handles.callback(kernel.CloseHandle, handle)
        yield


def publish_release(stage, release):
    """Swap complete directories, restoring the old release if promotion fails."""
    reparse = release.exists() and getattr(release.lstat(), 'st_file_attributes', 0) & 0x400
    if release.is_symlink() or reparse or (release.exists() and not release.is_dir()):
        raise RuntimeError(f'{release} must be a regular directory, not a link or file.')
    lock = release.parent / '.release-publish.lock'
    try:
        lock.mkdir()
    except FileExistsError as error:
        raise RuntimeError('Another release update is running. If it was interrupted, '
                           'remove .release-publish.lock after it has stopped.') from error
    backup = release.parent / f'.release-backup-{uuid.uuid4().hex}'
    moved_old = False
    try:
        with windows_update_handles(release):
            if release.exists():
                release.rename(backup)
                moved_old = True
            try:
                stage.rename(release)
            except BaseException:
                if moved_old:
                    backup.rename(release)
                raise
    except BaseException as error:
        recovery = f' Previous files are preserved at {backup}.' if backup.exists() else ''
        raise RuntimeError('Release update failed. Unload Outfit Studio in Dalamud, '
                           'stop any conversion worker, and retry. Check folder write '
                           f'permissions if it still fails.{recovery} Cause: {error}') from error
    finally:
        lock.rmdir()
    if moved_old:
        try:
            shutil.rmtree(backup)
        except OSError:
            print(f'Warning: release updated; old files remain at {backup}. '
                  'Unload the plugin and stop its worker before removing that backup.', file=sys.stderr)


def package(root, worker=None, make_zip=False):
    root = Path(root).resolve()
    worker = Path(worker).resolve() if worker else root / 'artifacts/bundle/worker'
    plugin = root / 'src/OutfitStudio.Plugin/bin/Release'
    for file in [plugin / 'OutfitStudio.dll', plugin / 'OutfitStudio.json',
                 *(worker / name for name in WORKER_FILES)]:
        if not file.is_file():
            raise RuntimeError(f'Missing build file: {file}. Build the plugin and publish '
                               'the complete Windows x64 self-contained worker first.')
    for name in public_files():
        if not (root / name).is_file():
            raise RuntimeError(f'Missing public distribution file: {root / name}. '
                               'Restore the matching documentation and license notices before packaging.')
    verify_runtime_notices(root, worker)
    stage = Path(tempfile.mkdtemp(prefix='.release-stage-', dir=root))
    release = root / 'release'
    temporary = None
    try:
        for name in ('OutfitStudio.dll', 'OutfitStudio.json'):
            shutil.copy2(plugin / name, stage / name)
        shutil.copytree(worker, stage / 'worker', ignore=ignore_symbols)
        for name in public_files():
            destination = stage / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(root / name, destination)
        if make_zip:
            version = ET.parse(root / 'src/OutfitStudio.Plugin/OutfitStudio.Plugin.csproj').findtext('.//Version')
            artifacts = root / 'artifacts'
            artifacts.mkdir(exist_ok=True)
            archive = artifacts / f'OutfitStudio-{version}.zip'
            temporary = artifacts / f'.package-{uuid.uuid4().hex}.zip'
            with zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as output:
                for file in sorted(stage.rglob('*')):
                    if file.is_file():
                        output.write(file, file.relative_to(stage))
        publish_release(stage, release)
        print(f'Dalamud dev plugin location: {release / "OutfitStudio.dll"}')
        if temporary:
            temporary.replace(archive)
            print(f'Optional ZIP: {archive}')
    finally:
        if stage.exists():
            shutil.rmtree(stage)
        if temporary:
            temporary.unlink(missing_ok=True)
    return release


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker', type=Path, help='Fresh published worker directory; defaults to artifacts/bundle/worker')
    parser.add_argument('--zip', action='store_true', help='Also export artifacts/OutfitStudio-VERSION.zip')
    args = parser.parse_args()
    try:
        package(Path(__file__).resolve().parents[1], args.worker, args.zip)
    except (OSError, RuntimeError) as error:
        raise SystemExit(str(error))
