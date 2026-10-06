"""Publication checks using synthetic files; never builds or updates release/."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile


spec = importlib.util.spec_from_file_location('outfitstudio_package', Path(__file__).parents[1] / 'scripts/package.py')
packaging = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packaging)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.worker = self.root / 'published-worker'
        self.worker.mkdir()
        plugin = self.root / 'src/OutfitStudio.Plugin/bin/Release'
        plugin.mkdir(parents=True)
        for name in ('OutfitStudio.dll', 'OutfitStudio.json'):
            (plugin / name).write_text('synthetic plugin fixture')
        for name in packaging.WORKER_FILES:
            (self.worker / name).write_text('synthetic worker fixture')
        (self.worker / 'OutfitStudio.Worker.deps.json').write_text(json.dumps({
            'libraries': {'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12': {}}}))
        (self.worker / 'OutfitStudio.Worker.runtimeconfig.json').write_text(json.dumps({
            'runtimeOptions': {'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.12'}]}}))
        for name in packaging.public_files():
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text('10.0.12\n' if name.endswith('/version.txt') else 'public document fixture')
        (self.root / 'src/OutfitStudio.Plugin/OutfitStudio.Plugin.csproj').write_text(
            '<Project><PropertyGroup><Version>0.1.9</Version></PropertyGroup></Project>')

    def test_zip_excludes_stale_symbols_and_unlisted_docs_and_includes_matching_notices(self):
        (self.worker / 'OutfitStudio.Worker.pdb').write_text('private source paths')
        nested = self.worker / 'nested'
        nested.mkdir()
        (nested / 'OutfitStudio.Core.PDB').write_text('private source paths')
        (self.root / 'docs/private-notes.md').write_text('must not ship')
        packaging.package(self.root, self.worker, make_zip=True)
        with zipfile.ZipFile(self.root / 'artifacts/OutfitStudio-0.1.9.zip') as archive:
            names = set(archive.namelist())
            self.assertFalse(any(Path(name).suffix.lower() == '.pdb' for name in names))
            self.assertNotIn('docs/private-notes.md', names)
            self.assertTrue(set(packaging.public_files()) <= names)
            self.assertTrue({'OutfitStudio.dll', 'OutfitStudio.json', 'worker/OutfitStudio.Worker.exe'} <= names)

    def test_runtime_upgrade_without_matching_notices_preserves_current_release(self):
        release = self.root / 'release'
        release.mkdir()
        (release / 'existing.txt').write_text('unchanged')
        (self.root / 'licenses/dotnet-host/version.txt').write_text('10.0.11\n')
        with self.assertRaisesRegex(RuntimeError, 'dotnet-host notices are for 10.0.11'):
            packaging.package(self.root, self.worker, make_zip=True)
        self.assertEqual('unchanged', (release / 'existing.txt').read_text())
        self.assertFalse((self.root / 'artifacts').exists())

    def test_runtime_config_must_match_published_dependency_version(self):
        (self.worker / 'OutfitStudio.Worker.runtimeconfig.json').write_text(json.dumps({
            'runtimeOptions': {'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.11'}]}}))
        with self.assertRaisesRegex(RuntimeError, 'runtime configuration and dependency versions differ'):
            packaging.package(self.root, self.worker)
        self.assertFalse((self.root / 'release').exists())


if __name__ == '__main__':
    unittest.main()
