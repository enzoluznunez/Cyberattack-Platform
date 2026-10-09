"""The Unity tools and the API would otherwise repeat the same constraints in
two languages. These tests fail if the generated file drifts from the OpenAPI
schema, or the schema from metrics.py."""

import codegen
from metrics import (
    ATTACK_TYPES,
    COUNTRIES,
    DEFAULT_VIEW,
    DIVISION_NAMES,
    FIRST_YEAR,
    INFORMATION_ACCESSED,
    LAST_YEAR,
    SHEET_LIMIT,
    VIEWS,
)


def test_generated_file_is_current():
    assert codegen.TARGET.exists(), "run: python codegen.py"
    assert codegen.TARGET.read_text() == codegen.render(codegen.schema())


def test_generated_years_and_limits_match_the_pydantic_models():
    text = codegen.TARGET.read_text()
    assert f"FirstYear = {FIRST_YEAR};" in text
    assert f"LastYear = {LAST_YEAR};" in text
    assert f"LimitDefault = {SHEET_LIMIT};" in text
    assert f'DefaultView = "{DEFAULT_VIEW}";' in text


def test_generated_names_are_the_ones_metrics_lists():
    text = codegen.TARGET.read_text()
    for names in (list(VIEWS), DIVISION_NAMES, ATTACK_TYPES, INFORMATION_ACCESSED, list(COUNTRIES)):
        assert all(f'"{name}"' in text for name in names)
