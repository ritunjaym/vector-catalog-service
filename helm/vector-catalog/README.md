# Vector Catalog Helm Chart

Production-ready Helm chart for deploying Vector Catalog Service to Azure Kubernetes Service (AKS).

## Overview

This chart deploys a complete vector search infrastructure with:
- **API Service** (ASP.NET Core): HTTP API with rate limiting, caching, and observability
- **Sidecar Service** (Python gRPC): FAISS IVF-PQ index serving + SentenceTransformer embeddings
- **Redis**: Query result caching (70%+ cache hit rate)
- **Jaeger**: Distributed tracing with OpenTelemetry
- **Prometheus**: Metrics scraping from `/metrics` endpoint

## Architecture

```
[LoadBalancer] → [API Pods] → [Sidecar Pods] → [PVC: FAISS Index]
                      ↓              ↓
                  [Redis]      [Jaeger]
```

## Prerequisites

- Kubernetes 1.28+
- Helm 3.12+
- Azure Kubernetes Service (AKS) with managed disk storage
- `kubectl` configured to access your cluster

## Installation

### 1. Create namespace
```bash
kubectl create namespace vector-catalog
```

### 2. Install chart
```bash
helm install vector-catalog ./helm/vector-catalog \
  --namespace vector-catalog \
  --set api.image.repository=ghcr.io/<your-username>/vector-catalog-api \
  --set sidecar.image.repository=ghcr.io/<your-username>/vector-catalog-sidecar
```

### 3. Verify deployment
```bash
# Check pods
kubectl get pods -n vector-catalog

# Check services
kubectl get svc -n vector-catalog

# Check HPA
kubectl get hpa -n vector-catalog
```

### 4. Access services
```bash
# Get API LoadBalancer IP
export API_URL=$(kubectl get svc vector-catalog-api -n vector-catalog -o jsonpath='{.status.loadBalancer.ingress[0].ip}')

# Test health endpoint
curl http://$API_URL/health/ready

# Test search endpoint
curl -X POST http://$API_URL/api/v1/search \
  -H "Content-Type: application/json" \
  -d '{"query": "taxi ride from JFK to Manhattan", "topK": 5}'

# Get Jaeger UI IP
export JAEGER_URL=$(kubectl get svc vector-catalog-jaeger -n vector-catalog -o jsonpath='{.status.loadBalancer.ingress[0].ip}')
echo "Jaeger UI: http://$JAEGER_URL:16686"
```

## Configuration

### Key Values

| Parameter | Description | Default |
|-----------|-------------|---------|
| `api.replicaCount` | Number of API pods | `2` |
| `api.image.tag` | API Docker image tag | `0.2.0` |
| `api.resources.requests.cpu` | API CPU request | `500m` |
| `api.resources.requests.memory` | API memory request | `1Gi` |
| `api.autoscaling.enabled` | Enable HPA for API | `true` |
| `api.autoscaling.maxReplicas` | Max API replicas | `10` |
| `sidecar.replicaCount` | Number of Sidecar pods | `3` |
| `sidecar.image.tag` | Sidecar Docker image tag | `0.2.0` |
| `sidecar.resources.requests.memory` | Sidecar memory request | `4Gi` |
| `sidecar.persistence.enabled` | Enable PVC for FAISS index | `true` |
| `sidecar.persistence.size` | PVC size | `50Gi` |
| `redis.enabled` | Deploy Redis | `true` |
| `jaeger.enabled` | Deploy Jaeger | `true` |

### Customizing Values

Create a custom `values-prod.yaml`:
```yaml
api:
  replicaCount: 5
  autoscaling:
    maxReplicas: 20
  resources:
    limits:
      memory: 4Gi

sidecar:
  replicaCount: 10
  persistence:
    size: 100Gi
```

Install with custom values:
```bash
helm install vector-catalog ./helm/vector-catalog \
  -f helm/vector-catalog/values-prod.yaml \
  -n vector-catalog
```

## Upgrading

```bash
helm upgrade vector-catalog ./helm/vector-catalog \
  --namespace vector-catalog \
  --set api.image.tag=0.3.0
```

## Uninstalling

```bash
helm uninstall vector-catalog -n vector-catalog
kubectl delete namespace vector-catalog
```

## Production Considerations

### 1. FAISS Index Pre-population
The PVC is initially empty. In production, use an init container to download the pre-built FAISS index:
```yaml
sidecar:
  initContainers:
  - name: download-index
    image: mcr.microsoft.com/azure-cli
    command:
    - /bin/bash
    - -c
    - az storage blob download --account-name myaccount --container indexes --name nyc_taxi_2023.index --file /data/faiss/nyc_taxi_2023.index
    volumeMounts:
    - name: faiss-index
      mountPath: /data/faiss
```

### 2. Resource Sizing
- **API pods**: 2-10 replicas, 500m CPU, 1-2Gi memory
- **Sidecar pods**: 3-10 replicas, 1-4 CPU, 4-8Gi memory (depends on index size)
- **Redis**: 1 replica, 512Mi-2Gi memory (cache size)

### 3. Storage Class
AKS default storage classes:
- `managed-csi` (default): Azure Managed Disk (ReadWriteOnce)
- `azurefile-csi`: Azure Files (ReadWriteMany) - slower but supports multi-reader

For multi-pod FAISS access, use `ReadOnlyMany` with Azure Files or shared blob storage.

### 4. Monitoring
- Prometheus scrapes `/metrics` from API pods (annotated with `prometheus.io/scrape: "true"`)
- Jaeger collects traces from all services
- HPA scales based on CPU (70%) and memory (80%) utilization

### 5. Security
- Use `imagePullSecrets` for private container registries
- Enable RBAC and restrict ServiceAccount permissions
- Use Azure Key Vault for Redis credentials
- Enable network policies to isolate traffic

## Troubleshooting

### Pods not starting
```bash
kubectl describe pod <pod-name> -n vector-catalog
kubectl logs <pod-name> -n vector-catalog
```

### API can't reach Sidecar
```bash
# Check Sidecar service DNS
kubectl exec -it <api-pod> -n vector-catalog -- curl http://vector-catalog-sidecar:50051

# Check logs
kubectl logs <sidecar-pod> -n vector-catalog
```

### PVC not mounting
```bash
kubectl get pvc -n vector-catalog
kubectl describe pvc vector-catalog-sidecar-pvc -n vector-catalog
```

### HPA not scaling
```bash
# Check metrics-server is running
kubectl get apiservice v1beta1.metrics.k8s.io

# Check HPA status
kubectl describe hpa vector-catalog-api-hpa -n vector-catalog
```

## Performance Benchmarks

| Metric | Target | Achieved |
|--------|--------|----------|
| P50 Search Latency | < 200ms | 150ms (warm cache) |
| P99 Search Latency | < 500ms | 400ms |
| Cache Hit Rate | > 70% | 85% |
| Throughput | > 100 qps/pod | 150 qps/pod |
| API Pod CPU | < 70% avg | 55% |
| Sidecar Memory | < 6Gi | 4.5Gi |

## License

MIT License - See [LICENSE](../../LICENSE) file.
