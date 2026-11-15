SELECT ci.CityName, sp.StateProvinceName FROM Application.Cities ci JOIN Application.StateProvinces sp ON sp.StateProvinceID = ci.StateProvinceID WHERE ci.CityID < 500
