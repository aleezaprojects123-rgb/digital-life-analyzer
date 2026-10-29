from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from .config import get_settings
from .routes import accounts, health

API_PREFIX = "/api/v1"


async def validation_error(request: Request, exc: RequestValidationError) -> JSONResponse:
    """FastAPI's normal 422 answer repeats the value that was sent, which would send a password back.
    This one says which field is wrong and why, and nothing else."""
    problems = [{"loc": list(e["loc"]), "msg": e["msg"], "type": e["type"]} for e in exc.errors()]
    return JSONResponse(status_code=422, content={"detail": problems})


def create_app() -> FastAPI:
    settings = get_settings()
    app = FastAPI(title="Digital Life Analyzer API", version="0.1.0")
    # Only the dashboard's own address may call the API from a browser.
    app.add_middleware(
        CORSMiddleware,
        allow_origins=settings.cors_origin_list,
        allow_methods=["GET", "POST", "PUT"],
        allow_headers=["Authorization", "Content-Type"],
    )
    app.add_exception_handler(RequestValidationError, validation_error)
    app.include_router(health.router, prefix=API_PREFIX)
    app.include_router(accounts.router, prefix=API_PREFIX)
    return app


app = create_app()
