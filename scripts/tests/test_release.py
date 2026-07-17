# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from scripts.release import (
    ReleaseError,
    VERSION_PATTERN,
    read_version,
    update_version,
)


class VersionPatternTests(unittest.TestCase):
    def test_accepts_supported_versions(self) -> None:
        versions = (
            "0.0.2",
            "1.2.3-alpha",
            "1.2.3-beta.2",
            "1.2.3-rc.1",
            "10.20.30-preview-1",
        )

        for version in versions:
            with self.subTest(version=version):
                self.assertIsNotNone(VERSION_PATTERN.fullmatch(version))

    def test_rejects_malformed_versions(self) -> None:
        versions = (
            "v1.2.3",
            "1.2",
            "01.2.3",
            "1.02.3",
            "1.2.03",
            "1.2.3-",
            "1.2.3-.",
            "1.2.3-alpha..1",
            "1.2.3-01",
            "1.2.3-alpha_1",
        )

        for version in versions:
            with self.subTest(version=version):
                self.assertIsNone(VERSION_PATTERN.fullmatch(version))


class VersionFileTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.props_path = Path(self.temporary_directory.name) / "Directory.Build.props"

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def write_props(self, content: str) -> None:
        self.props_path.write_text(content, encoding="utf-8")

    def test_reads_exactly_one_version(self) -> None:
        self.write_props(
            "<Project><PropertyGroup>"
            "<SharpEmuVersion>1.2.3-beta.2</SharpEmuVersion>"
            "</PropertyGroup></Project>"
        )

        self.assertEqual("1.2.3-beta.2", read_version(self.props_path))

    def test_rejects_missing_version(self) -> None:
        self.write_props("<Project><PropertyGroup /></Project>")

        with self.assertRaisesRegex(ReleaseError, "was not found"):
            read_version(self.props_path)

    def test_rejects_duplicate_versions(self) -> None:
        self.write_props(
            "<Project><PropertyGroup>"
            "<SharpEmuVersion>1.2.3</SharpEmuVersion>"
            "<SharpEmuVersion>2.0.0</SharpEmuVersion>"
            "</PropertyGroup></Project>"
        )

        with self.assertRaisesRegex(ReleaseError, "found 2"):
            read_version(self.props_path)

    def test_updates_the_version_without_changing_other_properties(self) -> None:
        self.write_props(
            "<Project>\n"
            "  <PropertyGroup>\n"
            "    <SharpEmuVersion>1.2.3</SharpEmuVersion>\n"
            "    <Version>$(SharpEmuVersion)</Version>\n"
            "  </PropertyGroup>\n"
            "</Project>\n"
        )

        previous_version = update_version(self.props_path, "1.2.4-rc.1")

        self.assertEqual("1.2.3", previous_version)
        self.assertEqual(
            "<Project>\n"
            "  <PropertyGroup>\n"
            "    <SharpEmuVersion>1.2.4-rc.1</SharpEmuVersion>\n"
            "    <Version>$(SharpEmuVersion)</Version>\n"
            "  </PropertyGroup>\n"
            "</Project>\n",
            self.props_path.read_text(encoding="utf-8"),
        )


if __name__ == "__main__":
    unittest.main()
