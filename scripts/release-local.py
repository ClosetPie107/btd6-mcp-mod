#!/usr/bin/env python3
"""Build a committed source snapshot; optionally publish a GitHub draft release."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile


def run(*args, cwd=None, capture=False, env=None):
    print('+', ' '.join(map(str, args)), flush=True)
    return subprocess.run(list(map(str, args)), cwd=cwd, env=env, check=True,
                          text=True, stdout=subprocess.PIPE if capture else None).stdout


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""Maintainer workflow:
  Requires Python 3.11.8+, Git, Node.js 20+, npm, the SDK from global.json,
  a prepared local game installation, and Mod Helper's btd6.targets.
  Set BTD6_GAME_DIR and BTD6_MOD_HELPER_TARGETS or pass the matching options.
  Set DOTNET or pass --dotnet /absolute/path/to/dotnet for a local SDK.

  Commit all intended release changes first. Dirty/untracked source is rejected.
  The tag labels the current commit; it does NOT rewrite ModHelperData.Version
  or the adapter package version. Update those before committing if needed.

  Build without uploading (replace the example tag and installed versions):
    python3 scripts/release-local.py v0.2.1 --local-only \\
      --game-version 56.3 --melonloader-version 0.7.3 --mod-helper-version 3.6.8

  Uploading additionally requires GitHub CLI (https://cli.github.com/).
  Run gh auth login with repository release and tag-push permissions.
  To build and upload a draft, replace --local-only with --repo OWNER/REPO.
  The selected push remote must match that GitHub repository.

  Builds run from an isolated committed snapshot, with managed regressions,
  adapter typechecks/tests and both release builds. Nothing is deployed to the
  game; no match is restarted. No game assemblies are bundled.

  Default output: ../release-artifacts/<tag>/ (outside the checkout)
    AgentBridge.dll, btd6-mcp-<tag>.zip, build-info.json, SHA256SUMS,
    release-notes.md (editable notes are not included in SHA256SUMS).
  The ZIP contains compiled JavaScript, package metadata/lockfile, PLAYING.md
  and INSTALL.txt. Users extract it and run npm ci --omit=dev.
  build-info.json records the commit, tool versions, declared dependency
  versions, selected build-reference hashes and verification scope.
  Version arguments declare the build environment, not verified compatibility.
  Local-only mode avoids GitHub access; dependency installation can need network.

  Existing output directories, tags and releases are not overwritten.
  After a local-only build, choose a different --output for a fresh upload
  build, or manually tag the recorded commit and upload those exact assets.
  Existing output is never reused as trusted release input.

  Upload mode checks GitHub authentication and remote tags/releases first.
  Only after successful builds/checks does it create an annotated tag at the
  captured commit, push only that tag, and create a DRAFT release.
  Test the exact uploaded DLL and extracted adapter locally, then update the
  draft notes with tested versions/platforms before publishing through GitHub:
    gh release edit v0.2.1 --repo OWNER/REPO --draft=false

  On push/upload failure, assets and any created tags/drafts are retained.
  Inspect the state and finish the missing push/upload manually; there is no
  destructive rollback. Use a new version instead of replacing public assets.
""")
    parser.add_argument('version', help='New release tag, e.g. v0.2.1 (does not rewrite source versions)')
    parser.add_argument('--local-only', action='store_true', help='Build assets without GitHub access or creating a tag')
    parser.add_argument('--repo', help='GitHub OWNER/REPO; required for uploading')
    parser.add_argument('--remote', default='origin', help='Git remote to push the release tag to')
    parser.add_argument('--output', type=Path, help='New output directory; default: sibling release-artifacts/VERSION')
    parser.add_argument('--game-dir', default=os.getenv('BTD6_GAME_DIR'))
    parser.add_argument('--mod-helper-targets', default=os.getenv('BTD6_MOD_HELPER_TARGETS'))
    parser.add_argument('--dotnet', default=os.getenv('DOTNET', 'dotnet'), help='SDK executable or absolute path')
    parser.add_argument('--game-version', required=True, help='Declared local BTD6 version used for compilation')
    parser.add_argument('--melonloader-version', required=True)
    parser.add_argument('--mod-helper-version', required=True)
    args = parser.parse_args()
    if sys.version_info < (3, 11, 8):
        parser.error('Python 3.11.8+ is required')
    if not re.fullmatch(r'v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?', args.version):
        parser.error('version must be a v-prefixed semantic release tag')
    if not args.local_only and (not args.repo or not re.fullmatch(r'[\w.-]+/[\w.-]+', args.repo)):
        parser.error('--repo OWNER/REPO is required for uploading')
    for name in ('git', 'node', 'npm', args.dotnet, *(() if args.local_only else ('gh',))):
        if not shutil.which(name):
            parser.error(f'Missing executable: {name}')
    if not args.game_dir or not args.mod_helper_targets:
        parser.error('Set --game-dir and --mod-helper-targets (or BTD6_GAME_DIR and BTD6_MOD_HELPER_TARGETS)')
    game = Path(args.game_dir).resolve()
    targets = Path(args.mod_helper_targets).resolve()
    references = [targets, game / 'MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll',
                  game / 'MelonLoader/net6/MelonLoader.dll', game / 'Mods/Btd6ModHelper.dll']
    for path in references:
        if not path.is_file():
            parser.error(f'Missing build dependency: {path}')
    root = Path(run('git', 'rev-parse', '--show-toplevel', cwd=Path(__file__).resolve().parent, capture=True).strip())
    if run('git', 'status', '--porcelain', '--untracked-files=all', cwd=root, capture=True).strip():
        parser.error('Source tree is dirty; commit the intended release contents first')
    commit = run('git', 'rev-parse', 'HEAD', cwd=root, capture=True).strip()
    if run('git', 'tag', '--list', args.version, cwd=root, capture=True).strip():
        parser.error(f'Tag already exists: {args.version}')
    output = (args.output or root.parent / 'release-artifacts' / args.version).resolve()
    if output.exists():
        parser.error(f'Output already exists: {output}')
    if output == root or root in output.parents:
        parser.error('Output must be outside the source checkout')
    if not args.local_only:
        run('gh', 'auth', 'status')
        remote_url = run('git', 'remote', 'get-url', '--push', args.remote, cwd=root, capture=True).strip()
        allowed_urls = (f'git@github.com:{args.repo}', f'https://github.com/{args.repo}', f'ssh://git@github.com/{args.repo}')
        if remote_url.removesuffix('.git') not in allowed_urls:
            parser.error('--repo must match the selected GitHub push remote')
        if run('git', 'ls-remote', '--tags', remote_url, f'refs/tags/{args.version}', cwd=root, capture=True).strip():
            parser.error('Remote tag already exists')
        releases = run('gh', 'api', '--paginate', f'repos/{args.repo}/releases', '--jq', '.[].tag_name', capture=True).splitlines()
        if args.version in releases:
            parser.error('GitHub release already exists')
    dotnet = str(Path(shutil.which(args.dotnet)).resolve())
    build_env = dict(os.environ, DOTNET_ROOT=str(Path(dotnet).parent), DOTNET_ROOT_X64=str(Path(dotnet).parent),
                     DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', CI='true')
    with tempfile.TemporaryDirectory(prefix='btd6-release-') as temp:
        temp = Path(temp)
        source = temp / 'source'
        source.mkdir()
        archive = temp / 'source.tar'
        run('git', 'archive', '--format=tar', f'--output={archive}', commit, cwd=root)
        with tarfile.open(archive) as tar:
            tar.extractall(source, filter='data')
        adapter = source / 'btd6-mcp'
        sdk = run(dotnet, '--version', cwd=source, capture=True, env=build_env).strip()
        run(dotnet, 'run', '--project', 'AgentBridge.Tests/AgentBridge.Tests.csproj', '-c', 'Release', cwd=source, env=build_env)
        run(dotnet, 'build', 'AgentBridge/AgentBridge.csproj', '-c', 'Release',
            f'-p:BloonsTD6={game}', f'-p:ModHelperTargets={targets}',
            '-p:GenerateActionsWorkflow=false', '-p:GenerateGitHooks=false', cwd=source, env=build_env)
        for command in [('ci',), ('run', 'check'), ('run', 'check:test'), ('test',), ('run', 'build')]:
            run('npm', *command, cwd=adapter, env=build_env)
        assets = temp / 'assets'
        assets.mkdir()
        dll = source / 'AgentBridge/bin/Release/AgentBridge.dll'
        shutil.copy2(dll, assets / 'AgentBridge.dll')
        package = temp / 'package'
        package.mkdir()
        shutil.copytree(adapter / 'dist', package / 'dist')
        for name in ('package.json', 'package-lock.json', 'PLAYING.md'):
            shutil.copy2(adapter / name, package / name)
        install = ('Install Node.js 20+; run npm ci --omit=dev in this directory.\n'
                   'Configure your MCP host to run node /absolute/path/to/dist/index.js\n'
                   'with BTD6_AGENT_BRIDGE_IPC_ROOT pointing to the game UserData/AgentBridge/ipc directory.\n'
                   'Install AgentBridge.dll into Mods alongside Btd6ModHelper.dll with the game stopped.\n'
                   'MelonLoader and its required .NET runtime must already be installed.\n')
        (package / 'INSTALL.txt').write_text(install)
        zip_path = assets / f'btd6-mcp-{args.version}.zip'
        with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as zipped:
            for path in sorted(package.rglob('*')):
                if path.is_file():
                    zipped.write(path, path.relative_to(package))
        metadata = {'tag': args.version, 'commit': commit, 'dotnetSdk': sdk,
                    'node': run('node', '--version', capture=True).strip(),
                    'declaredBuildVersions': {'btd6': args.game_version, 'melonloader': args.melonloader_version,
                                              'modHelper': args.mod_helper_version},
                    'buildReferenceSha256': {str(p.relative_to(game)) if p != targets else 'btd6.targets': sha256(p) for p in references},
                    'verification': ['managed regressions', 'adapter typechecks', 'adapter tests', 'release builds'],
                    'liveVerification': 'Not performed by release script'}
        (assets / 'build-info.json').write_text(json.dumps(metadata, indent=2) + '\n')
        (assets / 'SHA256SUMS').write_text(''.join(f'{sha256(p)}  {p.name}\n' for p in sorted(assets.iterdir())))
        notes = (f'Built from `{commit}`.\n\nDeclared build environment: BTD6 {args.game_version}, '
                 f'MelonLoader {args.melonloader_version}, BTD Mod Helper {args.mod_helper_version}.\n\n'
                 'Offline checks passed. Live compatibility has not been verified by this script; '
                 'test these exact assets and update these notes before publishing.\n\n'
                 '### Installation\n\n' + install)
        (assets / 'release-notes.md').write_text(notes)
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(assets, output)
    print(f'Release assets: {output}', flush=True)
    if args.local_only:
        return
    # No remote mutation occurs before all builds, checks and packaging succeed.
    run('git', 'tag', '-a', args.version, commit, '-m', f'Release {args.version}', cwd=root)
    run('git', 'push', args.remote, f'refs/tags/{args.version}:refs/tags/{args.version}', cwd=root)
    run('gh', 'release', 'create', args.version, *sorted(p for p in output.iterdir() if p.name != 'release-notes.md'),
        '--repo', args.repo, '--verify-tag', '--draft', '--title', args.version,
        '--notes-file', output / 'release-notes.md')
    print('Draft created. Test these exact assets before publishing. Tags/assets are never automatically deleted on failure.')


if __name__ == '__main__':
    try:
        main()
    except (subprocess.CalledProcessError, OSError, ValueError, tarfile.TarError) as error:
        print(f'Release failed: {error}\nNo automatic rollback: inspect any created tag/draft before retrying.', file=sys.stderr)
        sys.exit(1)
