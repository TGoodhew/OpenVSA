"""Fail on build-file mistakes that CI has already caught the expensive way.

Each check here corresponds to a red CI run. They are cheap, they only regress through an edit to a
project or solution file, and none of them is visible in a code review unless the reviewer happens
to know the rule.

  1. A pinned PlatformToolset. A .vcxproj naming v143 does not build on a machine with VS 2026, and
     one naming v145 does not build on the CI runner. $(DefaultPlatformToolset) is right on both.

  2. A C++ project in the solution. The dotnet CLI's MSBuild has no C++ targets, so
     "dotnet test OpenVSA.slnx" fails with MSB4278 while merely *evaluating* a .vcxproj -- it does
     not have to try to build it. Native projects are built from a target in the consuming csproj
     instead, guarded to full MSBuild.

  (A third trap is NOT checked here, deliberately. Exists() in the condition of an item that names
   a build output is evaluated before any target runs, so on a clean checkout the file does not
   exist yet, the item is dropped, and the copy silently never happens -- it fails only on a clean
   checkout, so every incremental local build passes. A check for it was written and then removed:
   the real instance reads Include="$(OpenVsaNativeFft)", a property reference, so a pattern over
   the literal text sees no build path and misses the very bug it was written for. Resolving
   properties across a project's imports to decide it properly is a bigger job than it looks, and a
   check that silently passes on its own motivating case is worse than none, because it manufactures
   confidence. The guard is the comment in OpenVSA.Dsp.csproj, and this paragraph.)

  4. Two output trees for one project. bin\\Debug\\net472 and bin\\x64\\Debug\\net472 both exist when
     a project is built with and without an explicit Platform, and "dotnet test --no-build" may then
     run whichever is stale. This one is not a build failure -- it is worse, because it makes tests
     appear to pass against code that was never rebuilt.

Not part of the product build. Run from the repository root:

    python tools/check_project_files.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOLUTION = os.path.join(ROOT, "OpenVSA.slnx")

SKIP_DIRECTORIES = ("bin", "obj", ".git", "packages", "artifacts")

TOOLSET_RE = re.compile(r"<PlatformToolset>\s*([^<]+?)\s*</PlatformToolset>", re.I)

def project_files():
    for directory, subdirectories, files in os.walk(ROOT):
        subdirectories[:] = [d for d in subdirectories if d not in SKIP_DIRECTORIES]

        for name in files:
            if name.endswith((".csproj", ".vcxproj", ".props", ".targets")):
                yield os.path.join(directory, name)


def main():
    problems = []

    # ---- 1. A pinned C++ toolset ------------------------------------------------------------
    for path in project_files():
        with open(path, encoding="utf-8-sig") as handle:
            text = handle.read()

        relative = os.path.relpath(path, ROOT)

        for toolset in TOOLSET_RE.findall(text):
            if "$(DefaultPlatformToolset)" not in toolset:
                problems.append(
                    "%s pins PlatformToolset to '%s'. Use $(DefaultPlatformToolset): this "
                    "repository is built on VS 2026 (v145) and on a CI runner with VS 2022 (v143), "
                    "and naming either breaks the other." % (relative, toolset)
                )


    # ---- 2. A C++ project in the solution ---------------------------------------------------
    if os.path.exists(SOLUTION):
        with open(SOLUTION, encoding="utf-8-sig") as handle:
            solution = handle.read()

        for match in re.findall(r"Path\s*=\s*\"([^\"]+\.vcxproj)\"", solution, re.I):
            problems.append(
                "OpenVSA.slnx lists %s. The dotnet CLI's MSBuild has no C++ targets and fails with "
                "MSB4278 while merely evaluating a .vcxproj, so 'dotnet test OpenVSA.slnx' cannot "
                "run at all. Build native projects from a target in the consuming project, guarded "
                "to full MSBuild." % match
            )

    # ---- 4. Two output trees for one project ------------------------------------------------
    for directory, subdirectories, files in os.walk(ROOT):
        if os.path.basename(directory) != "bin":
            continue

        subdirectories[:] = []

        for configuration in ("Debug", "Release"):
            plain = os.path.join(directory, configuration)
            platformed = os.path.join(directory, "x64", configuration)

            if os.path.isdir(plain) and os.path.isdir(platformed):
                problems.append(
                    "%s has both %s and x64\\%s. A build with an explicit Platform and one without "
                    "write to different trees, and 'dotnet test --no-build' may run whichever is "
                    "stale -- so tests can appear to pass against code that was never rebuilt. "
                    "Delete one, and build consistently."
                    % (os.path.relpath(directory, ROOT), configuration, configuration)
                )

    for problem in problems:
        print("  " + problem)
        print()

    print("%d problem(s)." % len(problems))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
