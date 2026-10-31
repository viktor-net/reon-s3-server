#!/usr/bin/env python3
"""Run all S3 smoke and security tests from the smoky_tests directory."""

import os
import sys
import unittest

HERE = os.path.dirname(os.path.realpath(__file__))
sys.path.insert(0, HERE)

suite = unittest.TestLoader().discover(
    HERE,
    pattern="test_*.py",
    top_level_dir=HERE,
)
result = unittest.TextTestRunner(verbosity=2).run(suite)
raise SystemExit(0 if result.wasSuccessful() else 1)
