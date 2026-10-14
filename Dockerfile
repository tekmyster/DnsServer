FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /opt/teqtanium-ddi
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY . .
ENV DNS_SERVER_WEB_SERVICE_LOCAL_ADDRESSES=0.0.0.0
ENV DNS_SERVER_LOG_FOLDER_PATH=/etc/dns/logs
ENTRYPOINT ["dotnet", "DnsServerApp.dll"]
CMD ["/etc/dns"]
VOLUME ["/etc/dns"]
EXPOSE 53/tcp 53/udp 5380/tcp 67/udp 853/tcp 853/udp 443/tcp 443/udp
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s CMD curl --fail http://127.0.0.1:5380/api/dnsClient/healthCheck || exit 1
LABEL org.opencontainers.image.title="teQtanium DDI Server"
LABEL org.opencontainers.image.source="https://github.com/tekmyster/teQtanium-DDI-Server"
LABEL org.opencontainers.image.licenses="GPL-3.0-only"
