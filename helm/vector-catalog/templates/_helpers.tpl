{{/*
Expand the name of the chart.
*/}}
{{- define "vector-catalog.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "vector-catalog.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "vector-catalog.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "vector-catalog.labels" -}}
helm.sh/chart: {{ include "vector-catalog.chart" . }}
{{ include "vector-catalog.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "vector-catalog.selectorLabels" -}}
app.kubernetes.io/name: {{ include "vector-catalog.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
API Service name
*/}}
{{- define "vector-catalog.api.name" -}}
{{- printf "%s-api" (include "vector-catalog.fullname" .) }}
{{- end }}

{{/*
Sidecar Service name
*/}}
{{- define "vector-catalog.sidecar.name" -}}
{{- printf "%s-sidecar" (include "vector-catalog.fullname" .) }}
{{- end }}

{{/*
Redis Service name
*/}}
{{- define "vector-catalog.redis.name" -}}
{{- printf "%s-redis" (include "vector-catalog.fullname" .) }}
{{- end }}

{{/*
Jaeger Service name
*/}}
{{- define "vector-catalog.jaeger.name" -}}
{{- printf "%s-jaeger" (include "vector-catalog.fullname" .) }}
{{- end }}
