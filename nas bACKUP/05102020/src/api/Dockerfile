# API image: build with the SDK, run on the slim ASP.NET runtime as the non-root "app" user.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY NasPos.sln ./
COPY NasPos.Domain/NasPos.Domain.csproj NasPos.Domain/
COPY NasPos.Application/NasPos.Application.csproj NasPos.Application/
COPY NasPos.Infrastructure/NasPos.Infrastructure.csproj NasPos.Infrastructure/
COPY NasPos.Api/NasPos.Api.csproj NasPos.Api/
COPY NasPos.Tests/NasPos.Tests.csproj NasPos.Tests/
RUN dotnet restore NasPos.Api/NasPos.Api.csproj
COPY . .
RUN dotnet publish NasPos.Api/NasPos.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
# QuestPDF needs fontconfig/freetype on Linux.
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "NasPos.Api.dll"]
