# Unified Documents Backend on Kubernetes

This bundle deploys only `Keyloop.UnifiedDocuments.Api`. Sales, Service, Redis, Elasticsearch/Kibana, Prometheus/Grafana, and Tempo are externally managed dependencies.

## Prerequisites and image

Install Docker and `kubectl`, build the image from the repository root, then replace the placeholder image in `deployment.yaml` with your registry name and immutable tag.

```sh
docker build -f src/backend/Keyloop.UnifiedDocuments.Api/Dockerfile -t ghcr.io/your-org/keyloop-unified-documents-api:1.0.0 .
docker push ghcr.io/your-org/keyloop-unified-documents-api:1.0.0
```

`configmap.yaml` contains non-sensitive configuration only. Replace its `.example.invalid` provider URLs and observability endpoints with environment-specific endpoints before applying it. The application uses `Providers__Sales__BaseUrl`, `Providers__Service__BaseUrl`, `Caching__*`, `Audit__Enabled`, `Audit__EventHubName`, and `Observability__ElasticsearchUrl`. Audit defaults to disabled; when enabled, the assessment mock publisher logs a small search-outcome event without attempting a network connection.

Create a real `unified-documents-api-secrets` Secret from a protected deployment pipeline or an external secret manager. `secret.example.yaml` is deliberately a template and must never contain production values. Redis connection details are secret because they commonly include credentials.

## Apply and verify

```sh
kubectl apply -f deploy/k8s/backend/namespace.yaml
kubectl apply -f deploy/k8s/backend/configmap.yaml
kubectl apply -f <real-secret-file>
kubectl apply -f deploy/k8s/backend/deployment.yaml
kubectl apply -f deploy/k8s/backend/service.yaml
kubectl apply -f deploy/k8s/backend/pdb.yaml
kubectl apply -f deploy/k8s/backend/hpa.yaml
kubectl rollout status deployment/unified-documents-api -n keyloop
kubectl get pods -n keyloop
kubectl get svc -n keyloop
kubectl logs deployment/unified-documents-api -n keyloop
kubectl port-forward svc/unified-documents-api 8080:80 -n keyloop
```

With the port-forward running, probe `http://localhost:8080/health/live`, `http://localhost:8080/health/ready`, and Prometheus metrics at `http://localhost:8080/metrics`. A future Ingress or API Gateway should route its backend to the `unified-documents-api` ClusterIP Service on port `80`, disable response buffering for the SSE route, and use an idle timeout longer than the configured interactive search budget so source events flush immediately.

## Operations

The baseline uses three replicas, a soft hostname spread constraint, a `minAvailable: 2` PDB, and CPU HPA from 3 to 10 replicas at 70% utilization. This is only a starting point: the service is I/O-bound and holds SSE connections, so active streams, request rate, latency, and provider concurrency are stronger future scaling signals.

The 512 MiB container limit and 32 MiB L1 cache budget are intentionally far apart. `MemoryCache.SizeLimit` uses application-defined serialized-payload units, not CLR heap accounting; tune the cache and container limit using production metrics. Each pod has its own bounded L1. The shared topology is `L1 -> Redis -> external provider`; successful origin responses write Redis then L1. L1 entries are not synchronized across pods, provider failures are never cached as empty values, and Redis errors fall back to direct provider access rather than making the pod unhealthy.

Sales and Service each have an independent, bounded per-pod concurrency limiter. The configured queue wait plus provider timeout must remain below the finite Redis distributed-lock lease; current operational defaults are 500 ms + 6 s for Sales and 750 ms + 10 s for Service, covered by the 30 s lease. Aggregate provider concurrency scales with replica count; a cluster-wide quota is a future concern only if an upstream provider requires it. SSE streams are short-lived one-search streams and do not use heartbeats or `Last-Event-ID` resume; clients retry the VIN search after a disconnect.

`/health/live` verifies only process liveness. `/health/ready` verifies that the started application can receive traffic; it intentionally does not call Redis or Sales/Service, because cache and provider outages are handled as application-level partial/fallback behavior rather than reasons to remove the pod from service.