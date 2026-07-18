import {
  copyFileSync,
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { dirname, relative, resolve } from "node:path";

const runtimeNodeModules = resolve(".next", "standalone", "node_modules");
const licenseOutput = resolve(".next", "standalone", "third-party-licenses");

// This demo does not use next/image and images.unoptimized is enabled. Next's
// server trace still includes its optional image optimizer dependencies, so
// remove them from the standalone distribution to avoid shipping unused native
// sharp/libvips binaries and their additional redistribution obligations.
for (const packagePath of ["sharp", "@img"]) {
  rmSync(resolve(runtimeNodeModules, packagePath), {
    recursive: true,
    force: true,
  });
}

function packageDirectories(nodeModulesDirectory) {
  if (!existsSync(nodeModulesDirectory)) {
    return [];
  }

  const directories = [];
  for (const entry of readdirSync(nodeModulesDirectory, { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name.startsWith(".")) {
      continue;
    }

    const entryPath = resolve(nodeModulesDirectory, entry.name);
    if (entry.name.startsWith("@")) {
      for (const scopedEntry of readdirSync(entryPath, { withFileTypes: true })) {
        if (scopedEntry.isDirectory()) {
          directories.push(resolve(entryPath, scopedEntry.name));
        }
      }
    } else {
      directories.push(entryPath);
    }
  }

  return directories.flatMap((packageDirectory) => [
    packageDirectory,
    ...packageDirectories(resolve(packageDirectory, "node_modules")),
  ]);
}

function licenseFiles(directory) {
  if (!existsSync(directory)) {
    return [];
  }

  return readdirSync(directory, { withFileTypes: true })
    .filter(
      (entry) =>
        entry.isFile() && /^(licen[cs]e|copying|notice)(\.|$)/i.test(entry.name),
    )
    .map((entry) => resolve(directory, entry.name));
}

function licenseFilesRecursively(directory) {
  if (!existsSync(directory)) {
    return [];
  }

  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const entryPath = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      return licenseFilesRecursively(entryPath);
    }

    return entry.isFile() && /^(licen[cs]e|copying|notice)(\.|$)/i.test(entry.name)
      ? [entryPath]
      : [];
  });
}

rmSync(licenseOutput, { recursive: true, force: true });
mkdirSync(licenseOutput, { recursive: true });

const packages = [];
for (const runtimePackageDirectory of packageDirectories(runtimeNodeModules)) {
  const packageJsonPath = resolve(runtimePackageDirectory, "package.json");
  if (!existsSync(packageJsonPath)) {
    continue;
  }

  const metadata = JSON.parse(readFileSync(packageJsonPath, "utf8"));
  if (!metadata.name || !metadata.version) {
    continue;
  }

  const licenseSourceName = metadata.name.startsWith("@next/")
    ? "next"
    : metadata.name === "client-only"
      ? "react"
      : metadata.name.startsWith("pg-") ||
          metadata.name.startsWith("postgres-") ||
          metadata.name === "pgpass"
        ? "pg"
        : metadata.name;
  const sourcePackageDirectory = resolve(
    "node_modules",
    ...licenseSourceName.split("/"),
  );
  const files = licenseFiles(sourcePackageDirectory);
  if (files.length === 0) {
    throw new Error(
      `No license file found for runtime package ${metadata.name}@${metadata.version}`,
    );
  }

  const outputDirectoryName = `${metadata.name.replaceAll("/", "__")}@${metadata.version}`;
  const packageLicenseOutput = resolve(licenseOutput, outputDirectoryName);
  mkdirSync(packageLicenseOutput, { recursive: true });

  for (const file of files) {
    copyFileSync(file, resolve(packageLicenseOutput, file.split(/[\\/]/).at(-1)));
  }

  packages.push({
    name: metadata.name,
    version: metadata.version,
    license: metadata.license ?? "See included license file",
    directory: outputDirectoryName,
  });
}

packages.sort((left, right) => left.name.localeCompare(right.name));

// Next.js vendors a number of compiled libraries below next/dist/compiled.
// Standalone tracing omits their adjacent license files, so preserve every
// license shipped in the installed Next.js package as a conservative superset.
const nextPackageDirectory = resolve("node_modules", "next");
const nextBundledLicenseOutput = resolve(licenseOutput, "next-bundled");
for (const file of licenseFilesRecursively(nextPackageDirectory)) {
  const destination = resolve(nextBundledLicenseOutput, relative(nextPackageDirectory, file));
  mkdirSync(dirname(destination), { recursive: true });
  copyFileSync(file, destination);
}

const notice = [
  "Field Service Demo - Third-Party Notices",
  "Copyright 2026 ClavisFlow",
  "",
  "The following packages are included in the standalone runtime image.",
  "Their complete license files are stored in the listed directories under",
  "third-party-licenses. The Field Service demo itself is licensed under the",
  "Apache License 2.0; see LICENSE.",
  "License files for libraries bundled inside Next.js are preserved under",
  "third-party-licenses/next-bundled.",
  "",
  ...packages.flatMap((packageInfo) => [
    `${packageInfo.name} ${packageInfo.version}`,
    `License: ${packageInfo.license}`,
    `License files: third-party-licenses/${packageInfo.directory}`,
    "",
  ]),
].join("\n");

writeFileSync(
  resolve(".next", "standalone", "THIRD-PARTY-NOTICES.txt"),
  notice,
  "utf8",
);

const projectLicenseSource = existsSync("LICENSE")
  ? "LICENSE"
  : resolve("node_modules", "@swc", "helpers", "LICENSE");
copyFileSync(projectLicenseSource, resolve(".next", "standalone", "LICENSE"));
