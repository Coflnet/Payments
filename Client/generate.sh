VERSION=0.1.0

docker run --rm -v "${PWD}:/local" --network host -u $(id -u ${USER}):$(id -g ${USER})  openapitools/openapi-generator-cli generate \
-i http://localhost:5000/swagger/v1/swagger.json \
-g csharp-netcore \
-o /local/out --additional-properties=packageName=Coflnet.Payments.Client,packageVersion=$VERSION,licenseId=MIT

cd out
sed -i 's/GIT_USER_ID/Coflnet/g' src/Coflnet.Payments.Client/Coflnet.Payments.Client.csproj
sed -i 's/GIT_REPO_ID/Payments/g' src/Coflnet.Payments.Client/Coflnet.Payments.Client.csproj
sed -i 's/>OpenAPI/>Coflnet/g' src/Coflnet.Payments.Client/Coflnet.Payments.Client.csproj

dotnet pack
cp src/Coflnet.Payments.Client/bin/Debug/Coflnet.Payments.Client.*.nupkg ..
