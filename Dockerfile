FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /App

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0
ENV TZ=Asia/Tokyo
WORKDIR /App
RUN apt-get update; apt-get install libfontconfig1 libfreetype6 libglib2.0-bin -y
COPY --from=build-env /App/Resources ./Resources
COPY --from=build-env /App/out .
ENTRYPOINT ["dotnet", "shake-detect-service.dll"]
