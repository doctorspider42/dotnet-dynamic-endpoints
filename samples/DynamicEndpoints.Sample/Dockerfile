# The sample app as a container – e.g. for a public live demo. Build from the repository root:
#
#   docker build -f samples/DynamicEndpoints.Sample/Dockerfile -t dynamic-endpoints-sample .
#   docker run --rm -p 8080:8080 dynamic-endpoints-sample            → http://localhost:8080/admin/
#
# Demo mode is on: a rate limit per client IP and a reset to the seeded endpoints every hour (Demo__ResetInterval).
# SQLite in /data by default; set ConnectionStrings__dynamicendpoints for PostgreSQL and ConnectionStrings__redis for
# several instances (see samples/docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ src/
COPY samples/DynamicEndpoints.ServiceDefaults/ samples/DynamicEndpoints.ServiceDefaults/
COPY samples/DynamicEndpoints.Sample/ samples/DynamicEndpoints.Sample/
RUN dotnet publish samples/DynamicEndpoints.Sample/DynamicEndpoints.Sample.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /data && chown app /data
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    ConnectionStrings__Default="Data Source=/data/dynamic-endpoints.db" \
    Demo__Enabled=true \
    Demo__ResetInterval=01:00:00 \
    Demo__RequestsPerMinute=120
VOLUME /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "DynamicEndpoints.Sample.dll"]
