# QuiVr Revival server for Linux hosts:  docker compose up -d
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/QuiVrRevival/QuiVrRevival.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
RUN mkdir /data && chown app /data
USER app
ENV QUIVR_HOST=0.0.0.0 QUIVR_PORT=4040 QUIVR_DATA=/data
VOLUME /data
EXPOSE 4040
ENTRYPOINT ["dotnet", "QuiVrRevival.dll", "serve"]
