# Build the Angular app into the API's wwwroot, publish the API, run it on the ASP.NET runtime.

FROM node:24.21.0-alpine AS web
WORKDIR /src
COPY package.json package-lock.json ./
COPY web/package.json web/
COPY e2e/package.json e2e/
RUN npm ci --workspace web
COPY web web
RUN npm run build --workspace web

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS api
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/LastSeat.Api/LastSeat.Api.csproj src/LastSeat.Api/
RUN dotnet restore src/LastSeat.Api/LastSeat.Api.csproj
COPY src/LastSeat.Api src/LastSeat.Api
COPY --from=web /src/src/LastSeat.Api/wwwroot src/LastSeat.Api/wwwroot
RUN dotnet publish src/LastSeat.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
COPY --from=api /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=5s --timeout=3s --retries=20 CMD ["dotnet", "LastSeat.Api.dll", "--health-check"]
ENTRYPOINT ["dotnet", "LastSeat.Api.dll"]
