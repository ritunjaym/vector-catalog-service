# Contributing to Vector Catalog Service

## Development Setup

```bash
git clone https://github.com/ritunjaym/vector-catalog-service
cd vector-catalog-service
```

### .NET API
```bash
dotnet restore
dotnet build
dotnet test
```

### Python Sidecar
```bash
cd sidecar
python3 -m venv venv
source venv/bin/activate
pip install -r requirements.txt
pytest tests/ -v
```

## Code Style

- **C#:** Follow `.editorconfig`, run `dotnet format`
- **Python:** Black formatter (`black .`), flake8 linter
- **Commits:** Conventional Commits (`feat:`, `fix:`, `docs:`)

## Pull Request Process

1. Fork repo, create feature branch: `git checkout -b feature/your-feature`
2. Write tests for new features (maintain >70% coverage)
3. Ensure CI passes (all 8 jobs green)
4. Update README if user-facing changes
5. Request review

## Running Locally

```bash
docker compose up -d
./scripts/run_demo.sh
```

## Reporting Issues

Use GitHub Issues with:
- Clear reproduction steps
- Expected vs actual behavior
- Environment (OS, Docker version)
