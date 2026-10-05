import { useState } from "react";

export function useProductForm() {
  const [productName, setProductName] = useState("");
  // picks UK or EU rules to check the copy against
  const [market, setMarket] = useState("UK");
  const [primaryCategory, setPrimaryCategory] = useState("");
  const [secondaryCategory, setSecondaryCategory] = useState("");
  const [productTags, setProductTags] = useState("");

  return {
    productName, setProductName,
    market, setMarket,
    primaryCategory, setPrimaryCategory,
    secondaryCategory, setSecondaryCategory,
    productTags, setProductTags,
  };
}
