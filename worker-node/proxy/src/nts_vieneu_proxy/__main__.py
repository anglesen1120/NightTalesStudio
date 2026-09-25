"""Entry point: ``python -m nts_vieneu_proxy`` (D1/D13).

Invalid config or catalog → print ``invalid config: <VAR>`` (or
``invalid catalog: <code>``) to stderr and exit 2; never the value.
"""

from __future__ import annotations

import os
import sys


def main() -> int:
    import uvicorn

    from .app import create_app
    from .catalog import load_catalog
    from .config import Settings
    from .errors import CatalogError, ConfigError

    try:
        settings = Settings.from_env(os.environ)
    except ConfigError as exc:
        print(str(exc), file=sys.stderr)  # "invalid config: <VAR>"
        return 2

    try:
        catalog = load_catalog(settings.voice_catalog_file)
    except CatalogError as exc:
        print(f"invalid catalog: {exc.code}", file=sys.stderr)
        return 2

    app = create_app(settings, catalog)
    uvicorn.run(
        app,
        host=settings.proxy_host,
        port=settings.proxy_port,
        access_log=False,  # D7: the reqlog line is the only access log
        log_level="warning",
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
